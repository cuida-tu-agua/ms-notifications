using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.ShortMessages;
using SyWater.Notifications.Infrastructure.ShortMessages;

namespace SyWater.Notifications.Infrastructure.Tests;

public class TwilioTemplateTests
{
    private sealed class Stub(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static TwilioShortMessageSender Sender(Stub stub, string template = "", string codeTemplate = "", string smsFrom = "") =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://twilio.test/") },
            Options.Create(new TwilioOptions
            {
                AccountSid = "ACtest", AuthToken = "secret", SmsFrom = smsFrom, WhatsAppFrom = "+15551234567",
                WhatsAppTemplateSid = template, CodeTemplateSid = codeTemplate,
            }));

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private const string Accepted = """{"sid":"SM42","status":"queued"}""";

    [Fact]
    public async Task With_an_approved_template_the_whole_text_goes_in_its_only_variable_in_one_line()
    {
        var stub = new Stub(HttpStatusCode.Created, Accepted);

        await Sender(stub, template: "HXalert").SendAsync(
            new ShortMessage("+573001112233", ShortMessageChannel.WhatsApp, "Alerta\nen   Casa centro"), default);

        var form = Form(stub.Body);
        Assert.Equal("whatsapp:+573001112233", form["To"]);
        Assert.Equal("whatsapp:+15551234567", form["From"]);
        Assert.Equal("HXalert", form["ContentSid"]);
        Assert.Equal("Alerta en Casa centro", JsonDocument.Parse(form["ContentVariables"]).RootElement.GetProperty("1").GetString());
        Assert.False(form.ContainsKey("Body"));
    }

    [Fact]
    public async Task A_code_goes_with_the_authentication_template()
    {
        var stub = new Stub(HttpStatusCode.Created, Accepted);

        await Sender(stub, template: "HXalert", codeTemplate: "HXcode").SendAsync(
            new ShortMessage("+593991112233", ShortMessageChannel.WhatsApp, "Tu código es 123456", ShortMessageKind.VerificationCode, "123456"), default);

        var form = Form(stub.Body);
        Assert.Equal("HXcode", form["ContentSid"]);
        Assert.Equal("123456", JsonDocument.Parse(form["ContentVariables"]).RootElement.GetProperty("1").GetString());
    }

    [Fact]
    public async Task Without_a_template_it_is_free_text_as_before()
    {
        var stub = new Stub(HttpStatusCode.Created, Accepted);

        await Sender(stub).SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.WhatsApp, "Hola"), default);

        var form = Form(stub.Body);
        Assert.Equal("Hola", form["Body"]);
        Assert.False(form.ContainsKey("ContentSid"));
    }

    [Fact]
    public async Task Without_an_sms_number_an_sms_is_refused_for_good()
    {
        var stub = new Stub(HttpStatusCode.Created, Accepted);

        await Assert.ThrowsAsync<ShortMessageRejectedException>(() =>
            Sender(stub).SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.Sms, "Hola"), default));
        Assert.Null(stub.Last);
    }

    [Fact]
    public async Task The_status_that_Twilio_reports_later_is_read_with_its_error()
    {
        var stub = new Stub(HttpStatusCode.OK,
            """{"sid":"SM42","status":"undelivered","error_code":63016,"error_message":"Failed to send freeform message because you are outside the allowed window."}""");

        var status = await Sender(stub).ReadAsync("SM42", default);

        Assert.NotNull(status);
        Assert.True(status.Failed);
        Assert.True(status.Final);
        Assert.Contains("63016", status.Error);
        Assert.Equal(HttpMethod.Get, stub.Last!.Method);
        Assert.EndsWith("/2010-04-01/Accounts/ACtest/Messages/SM42.json", stub.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_delivered_message_is_final_and_an_id_of_another_provider_is_not_asked()
    {
        var stub = new Stub(HttpStatusCode.OK, """{"sid":"SM42","status":"delivered","error_code":null}""");

        var delivered = await Sender(stub).ReadAsync("SM42", default);
        Assert.False(delivered!.Failed);
        Assert.True(delivered.Final);

        var stub2 = new Stub(HttpStatusCode.OK, "{}");
        Assert.Null(await Sender(stub2).ReadAsync("wamid.HBgM", default));
        Assert.Null(stub2.Last);
    }
}
