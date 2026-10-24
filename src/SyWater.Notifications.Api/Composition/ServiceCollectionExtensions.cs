using Microsoft.EntityFrameworkCore;
using SyWater.Notifications.Api.Background;
using SyWater.Notifications.Api.Messaging;
using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.UseCases;
using SyWater.Notifications.Domain.Emails;
using SyWater.Notifications.Infrastructure.Email;
using SyWater.Notifications.Infrastructure.Http;
using SyWater.Notifications.Infrastructure.Persistence;
using SyWater.Notifications.Infrastructure.Push;
using SyWater.Notifications.Infrastructure.ShortMessages;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Api.Composition;

/// <summary>Composition root: the ONLY place that knows which adapter implements each port.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The e-mail channel is on only when an SMTP host is configured (Mailpit in development).</summary>
    public static bool EmailEnabled(IConfiguration config) => !string.IsNullOrWhiteSpace(config[$"{SmtpOptions.Section}:Host"]);

    /// <summary>The push channel is on unless Push:Enabled is false (it only calls Expo for users who registered a phone).</summary>
    public static bool PushEnabled(IConfiguration config) => config.GetValue("Push:Enabled", true);

    /// <summary>
    /// HU-028: Sms:Provider = "twilio" (production) · "mailpit" (development: every SMS becomes a mail in Mailpit) ·
    /// empty = the SMS / WhatsApp channel is off (nothing is queued; the verification code answers 503 sms.disabled).
    /// </summary>
    public static string SmsProvider(IConfiguration config)
    {
        var provider = (config["Sms:Provider"] ?? "").Trim().ToLowerInvariant();
        return provider is "" or "twilio" or "mailpit"
            ? provider
            : throw new InvalidOperationException($"Unknown 'Sms:Provider' '{provider}'. Use twilio, mailpit or leave it empty.");
    }

    /// <summary>
    /// HU-028: WhatsApp:Provider = "meta" (WhatsApp Cloud API of Meta, section "Meta") · empty = WhatsApp goes by the same
    /// provider as SMS (Sms:Provider). With "meta" and an empty Sms:Provider only WhatsApp works.
    /// </summary>
    public static string WhatsAppProvider(IConfiguration config)
    {
        var provider = (config["WhatsApp:Provider"] ?? "").Trim().ToLowerInvariant();
        return provider is "" or "meta"
            ? provider
            : throw new InvalidOperationException($"Unknown 'WhatsApp:Provider' '{provider}'. Use meta or leave it empty.");
    }

    public static bool SmsEnabled(IConfiguration config) => SmsProvider(config) != "" || WhatsAppProvider(config) != "";

    public static IServiceCollection AddNotificationsApplication(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new EmailSettings(EmailEnabled(config), config["App:PublicUrl"] ?? "http://localhost:8081"));
        services.AddSingleton(EmailRetryPolicy.Default);
        services.AddSingleton(new PushSettings(PushEnabled(config)));
        services.AddSingleton(new SmsSettings(SmsEnabled(config)));
        services.AddSingleton(SmsRetryPolicy.Default);
        services.AddScoped<PushNotifier>();
        services.AddSingleton(new OfflineSettings(TimeSpan.FromMinutes(config.GetValue("Devices:OfflineAfterMinutes", 10))));
        services.AddScoped<IListMyNotificationsUseCase, ListMyNotificationsUseCase>();
        services.AddScoped<IGetUnreadCountUseCase, GetUnreadCountUseCase>();
        services.AddScoped<IMarkNotificationReadUseCase, MarkNotificationReadUseCase>();
        services.AddScoped<IMarkAllNotificationsReadUseCase, MarkAllNotificationsReadUseCase>();
        services.AddScoped<IGetMyPreferencesUseCase, GetMyPreferencesUseCase>();
        services.AddScoped<IUpdateMyPreferencesUseCase, UpdateMyPreferencesUseCase>();
        services.AddScoped<IRegisterPushTokenUseCase, RegisterPushTokenUseCase>();
        services.AddScoped<IUnregisterPushTokenUseCase, UnregisterPushTokenUseCase>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<IDeviceEventsHandler, DeviceEventsHandler>();
        services.AddScoped<ISendDueEmailsUseCase, SendDueEmailsUseCase>();
        services.AddScoped<ISendDueShortMessagesUseCase, SendDueShortMessagesUseCase>();
        services.AddScoped<ISendVerificationCodeUseCase, SendVerificationCodeUseCase>();
        services.AddScoped<IDetectOfflineDevicesUseCase, DetectOfflineDevicesUseCase>();
        services.AddScoped<MessageTexts>();
        services.AddScoped<IListMessageTemplatesUseCase, ListMessageTemplatesUseCase>();
        services.AddScoped<IUpdateMessageTemplateUseCase, UpdateMessageTemplateUseCase>();
        services.AddScoped<IResetMessageTemplateUseCase, ResetMessageTemplateUseCase>();
        services.AddScoped<IGetSharedMessageTemplateUseCase, GetSharedMessageTemplateUseCase>();
        services.AddScoped<INotifyCollaboratorInvitationUseCase, NotifyCollaboratorInvitationUseCase>();
        services.AddScoped<INotifyInvitationAnsweredUseCase, NotifyInvitationAnsweredUseCase>();
        services.AddScoped<ISendTestMessageUseCase, SendTestMessageUseCase>();
        services.AddSingleton<IGetShortMessageProvidersUseCase, GetShortMessageProvidersUseCase>();
        services.AddSingleton(new ShortMessageProviders(
            SmsProvider(config),
            WhatsAppProvider(config) == "" ? SmsProvider(config) : WhatsAppProvider(config),
            WhatsAppProvider(config) == "meta" && !string.IsNullOrWhiteSpace(config["Meta:TemplateName"])));
        return services;
    }

    /// <summary>RabbitMQ consumer (empty RabbitMq:Host = not started), the e-mail worker (only if Smtp:Host is set) and the offline-device detector.</summary>
    public static IServiceCollection AddNotificationsBackground(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RabbitMqOptions>(config.GetSection(RabbitMqOptions.Section));
        if (!string.IsNullOrWhiteSpace(config[$"{RabbitMqOptions.Section}:Host"]))
            services.AddHostedService<DeviceEventsConsumer>();
        if (EmailEnabled(config))
            services.AddHostedService<EmailDeliveryWorker>();
        if (SmsEnabled(config))
            services.AddHostedService<SmsDeliveryWorker>();
        services.AddHostedService<OfflineDeviceWorker>();
        return services;
    }

    public static IServiceCollection AddNotificationsInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Notification");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Notification' (user-secrets).");

        services.AddDbContext<NotificationDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<INotificationRepository, EfNotificationRepository>();
        services.AddScoped<IPlaceDeviceRepository, EfPlaceDeviceRepository>();
        services.AddScoped<INotificationPreferenceRepository, EfNotificationPreferenceRepository>();
        services.AddScoped<IEmailOutboxRepository, EfEmailOutboxRepository>();
        services.AddScoped<IPushTokenRepository, EfPushTokenRepository>();
        services.AddScoped<ISmsOutboxRepository, EfSmsOutboxRepository>();
        services.AddScoped<IMessageTemplateRepository, EfMessageTemplateRepository>();

        services.AddHttpClient<IPushSender, ExpoPushSender>(http =>
        {
            http.BaseAddress = new Uri(config["Push:ExpoUrl"] ?? "https://exp.host/--/api/v2/push/send");
            http.Timeout = TimeSpan.FromSeconds(10);
            if (!string.IsNullOrWhiteSpace(config["Push:ExpoAccessToken"]))   // optional: only if the Expo account enabled enhanced security
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config["Push:ExpoAccessToken"]);
        });

        if (EmailEnabled(config))
        {
            services.Configure<SmtpOptions>(config.GetSection(SmtpOptions.Section));
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }

        // The e-mail asks ms-iam for the address; SMS / WhatsApp asks for the verified phone and its mode
        if (EmailEnabled(config) || SmsEnabled(config))
        {
            var iamUrl = Required(config, "Services:IamBaseUrl");
            var internalKey = config["Internal:ApiKey"];
            if (string.IsNullOrWhiteSpace(internalKey) || internalKey.Length < 24)
                throw new InvalidOperationException(
                    "Missing 'Internal:ApiKey' (user-secrets, at least 24 chars). It must be the SAME value as in the other services.");

            services.AddHttpClient(HttpUserContactDirectory.ServiceName, http => Configure(http, iamUrl))
                .AddTypedClient<IUserContactDirectory>((http, _) => new HttpUserContactDirectory(http, internalKey));
        }

        // HU-047: collaborators with VIEW_ALERTS also get the alerts. Without ms-places configured only the owner is told.
        var placesUrl = config["Services:PlacesBaseUrl"];
        var sharedKey = config["Internal:ApiKey"];
        if (!string.IsNullOrWhiteSpace(placesUrl) && sharedKey is { Length: >= 24 })
        {
            services.AddHttpClient(HttpPlaceAlertRecipients.ServiceName, http => Configure(http, placesUrl))
                .AddTypedClient<IPlaceAlertRecipients>((http, _) => new HttpPlaceAlertRecipients(http, sharedKey));
        }

        AddShortMessageSender(services, config);
        return services;
    }

    /// <summary>
    /// HU-028: the adapter of Sms:Provider (see <see cref="SmsProvider"/>) and, when WhatsApp:Provider is "meta", the
    /// WhatsApp Cloud API of Meta for the WhatsApp messages (each mode by its own provider).
    /// </summary>
    private static void AddShortMessageSender(IServiceCollection services, IConfiguration config)
    {
        Type? smsSender = null;
        switch (SmsProvider(config))
        {
            case "twilio":
                var twilio = config.GetSection(TwilioOptions.Section);
                if (string.IsNullOrWhiteSpace(twilio["AccountSid"]) || string.IsNullOrWhiteSpace(twilio["AuthToken"]))
                    throw new InvalidOperationException("Sms:Provider is twilio: set 'Twilio:AccountSid' and 'Twilio:AuthToken' (user-secrets / infra .env).");
                if (string.IsNullOrWhiteSpace(twilio["SmsFrom"]) && string.IsNullOrWhiteSpace(twilio["WhatsAppFrom"]))
                    throw new InvalidOperationException("Sms:Provider is twilio: set 'Twilio:SmsFrom' and/or 'Twilio:WhatsAppFrom'.");
                services.Configure<TwilioOptions>(twilio);
                services.AddHttpClient<TwilioShortMessageSender>(http =>
                {
                    var options = twilio.Get<TwilioOptions>() ?? new TwilioOptions();
                    Configure(http, options.BaseUrl);
                    http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                });
                smsSender = typeof(TwilioShortMessageSender);
                break;
            case "mailpit":
                if (!EmailEnabled(config))
                    throw new InvalidOperationException("Sms:Provider is mailpit: it needs 'Smtp:Host' (the fake SMS are mails in Mailpit).");
                services.AddSingleton<MailpitShortMessageSender>();
                smsSender = typeof(MailpitShortMessageSender);
                break;
        }

        if (WhatsAppProvider(config) != "meta")
        {
            if (smsSender is null) services.AddSingleton<IShortMessageSender, DisabledShortMessageSender>();
            else services.AddTransient(typeof(IShortMessageSender), sp => sp.GetRequiredService(smsSender));
            return;
        }

        var meta = config.GetSection(MetaWhatsAppOptions.Section);
        if (string.IsNullOrWhiteSpace(meta["AccessToken"]) || string.IsNullOrWhiteSpace(meta["PhoneNumberId"]))
            throw new InvalidOperationException(
                "WhatsApp:Provider is meta: set 'Meta:AccessToken' and 'Meta:PhoneNumberId' (WhatsApp > API Setup of the Meta app; infra .env).");
        services.Configure<MetaWhatsAppOptions>(meta);
        services.AddHttpClient<MetaWhatsAppSender>(http =>
        {
            var options = meta.Get<MetaWhatsAppOptions>() ?? new MetaWhatsAppOptions();
            Configure(http, options.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        services.AddTransient<IShortMessageSender>(sp => new ChannelRoutingShortMessageSender(
            smsSender is null ? null : (IShortMessageSender)sp.GetRequiredService(smsSender),
            sp.GetRequiredService<MetaWhatsAppSender>()));
    }

    private static string Required(IConfiguration config, string key) =>
        string.IsNullOrWhiteSpace(config[key]) ? throw new InvalidOperationException($"Missing '{key}'.") : config[key]!;

    private static void Configure(HttpClient client, string baseUrl)
    {
        client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        client.Timeout = TimeSpan.FromSeconds(5);
    }
}
