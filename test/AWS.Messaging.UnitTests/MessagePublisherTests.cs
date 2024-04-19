// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AWS.Messaging.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
using Moq;
using AWS.Messaging.Publishers;
using System.Threading.Tasks;
using AWS.Messaging.UnitTests.Models;
using Amazon.SQS;
using AWS.Messaging.Serialization;
using System.Threading;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using AWS.Messaging.Publishers.EventBridge;
using AWS.Messaging.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using AWS.Messaging.Publishers.SQS;
using AWS.Messaging.Publishers.SNS;

namespace AWS.Messaging.UnitTests;

public class MessagePublisherTests
{
    private readonly Mock<IMessageConfiguration> _messageConfiguration;
    private readonly Mock<ILogger<IMessagePublisher>> _messagePublisherLogger;
    private readonly Mock<ILogger<ISQSPublisher>> _sqsPublisherLogger;
    private readonly Mock<IAmazonSQS> _sqsClient;
    private readonly Mock<IAmazonSimpleNotificationService> _snsClient;
    private readonly Mock<IAmazonEventBridge> _eventBridgeClient;
    private readonly Mock<IEnvelopeSerializer> _envelopeSerializer;
    private readonly ChatMessage _chatMessage;

    public MessagePublisherTests()
    {
        _messageConfiguration = new Mock<IMessageConfiguration>();
        _messagePublisherLogger = new Mock<ILogger<IMessagePublisher>>();
        _sqsPublisherLogger = new Mock<ILogger<ISQSPublisher>>();

        _sqsClient = new Mock<IAmazonSQS>();
        _sqsClient.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()));

        _snsClient = new Mock<IAmazonSimpleNotificationService>();
        _snsClient.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()));

        _eventBridgeClient = new Mock<IAmazonEventBridge>();
        _eventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>()));

        _envelopeSerializer = new Mock<IEnvelopeSerializer>();
        _envelopeSerializer.SetReturnsDefault(ValueTask.FromResult(new MessageEnvelope<ChatMessage>()
        {
            Id = "1234",
            Source = new Uri("/aws/messaging/unittest", UriKind.Relative)
        }));


        _chatMessage = new ChatMessage
        {
            MessageDescription = "Test Description"
        };
    }

    [Fact]
    public async Task SQSPublisher_HappyPath()
    {
        var serviceProvider = SetupSQSPublisherDIServices();

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );
        _sqsClient.Setup(x =>
            x.SendMessageAsync(
                It.Is<SendMessageRequest>(request =>
                    request.QueueUrl.Equals("endpoint")),
                It.IsAny<CancellationToken>())).ReturnsAsync(new SendMessageResponse()
        {
            MessageId = "MessageId"
        });

        var result = await messagePublisher.PublishAsync(_chatMessage);

        _sqsClient.Verify(x =>
                x.SendMessageAsync(
                    It.Is<SendMessageRequest>(request =>
                        request.QueueUrl.Equals("endpoint")),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));
        Assert.Equal("MessageId", result.MessageId);
    }

    [Fact]
    public async Task RoutingPublisher_TelemetryHappyPath()
    {
        var serviceProvider = SetupSQSPublisherDIServices();
        var telemetryFactory = new Mock<ITelemetryFactory>();
        var telemetryTrace = new Mock<ITelemetryTrace>();

        _sqsClient.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SendMessageResponse()
        {
            MessageId = "MessageId"
        });
        telemetryFactory.Setup(x => x.Trace(It.IsAny<string>())).Returns(telemetryTrace.Object);
        telemetryTrace.Setup(x => x.AddMetadata(It.IsAny<string>(), It.IsAny<string>()));

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            telemetryFactory.Object
        );

        var publishResponse = await messagePublisher.PublishAsync(_chatMessage);

        telemetryFactory.Verify(x =>
                x.Trace(
                    It.Is<string>(request =>
                        request.Equals("Routing message to AWS service"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.ObjectType)),
                    It.Is<string>(request =>
                        request.Equals("AWS.Messaging.UnitTests.Models.ChatMessage"))),
            Times.Exactly(1));
        Assert.Equal("MessageId", publishResponse.MessageId);
    }

    [Fact]
    public async Task RoutingPublisher_TelemetryThrowsException()
    {
        var serviceProvider = SetupSQSPublisherDIServices();
        var telemetryFactory = new Mock<ITelemetryFactory>();
        var telemetryTrace = new Mock<ITelemetryTrace>();

        _sqsClient.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("Telemetry exception"));
        telemetryFactory.Setup(x => x.Trace(It.IsAny<string>())).Returns(telemetryTrace.Object);
        telemetryTrace.Setup(x => x.AddMetadata(It.IsAny<string>(), It.IsAny<string>()));

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            telemetryFactory.Object
        );

        await Assert.ThrowsAsync<Exception>(() => messagePublisher.PublishAsync(_chatMessage));

        telemetryTrace.Verify(x =>
                x.AddException(
                    It.Is<Exception>(request =>
                        request.Message.Equals("Telemetry exception")),
                    It.IsAny<bool>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task SQSPublisher_TelemetryHappyPath()
    {
        var serviceProvider = SetupSQSPublisherDIServices();
        var telemetryFactory = new Mock<ITelemetryFactory>();
        var telemetryTrace = new Mock<ITelemetryTrace>();
        _sqsClient.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SendMessageResponse()
        {
            MessageId = "MessageId"
        });

        telemetryFactory.Setup(x => x.Trace(It.IsAny<string>())).Returns(telemetryTrace.Object);
        telemetryTrace.Setup(x => x.AddMetadata(It.IsAny<string>(), It.IsAny<string>()));

        var messagePublisher = new SQSPublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _sqsPublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            telemetryFactory.Object
        );

        var sendResult = await messagePublisher.SendAsync(_chatMessage);

        telemetryFactory.Verify(x =>
                x.Trace(
                    It.Is<string>(request =>
                        request.Equals("Publish to AWS SQS"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.ObjectType)),
                    It.Is<string>(request =>
                        request.Equals("AWS.Messaging.UnitTests.Models.ChatMessage"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.MessageType)),
                    It.Is<string>(request =>
                        request.Equals("AWS.Messaging.UnitTests.Models.ChatMessage"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.QueueUrl)),
                    It.Is<string>(request =>
                        request.Equals("endpoint"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.MessageId)),
                    It.Is<string>(request =>
                        request.Equals("1234"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.RecordTelemetryContext(
                    It.IsAny<MessageEnvelope>()),
            Times.Exactly(1));
        Assert.Equal("MessageId", sendResult.MessageId);
    }

    [Fact]
    public async Task SQSPublisher_TelemetryThrowsException()
    {
        var serviceProvider = SetupSQSPublisherDIServices();
        var telemetryFactory = new Mock<ITelemetryFactory>();
        var telemetryTrace = new Mock<ITelemetryTrace>();

        _sqsClient.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("Telemetry exception"));
        telemetryFactory.Setup(x => x.Trace(It.IsAny<string>())).Returns(telemetryTrace.Object);
        telemetryTrace.Setup(x => x.AddMetadata(It.IsAny<string>(), It.IsAny<string>()));

        var messagePublisher = new SQSPublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _sqsPublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            telemetryFactory.Object
        );

        await Assert.ThrowsAsync<Exception>(() => messagePublisher.SendAsync(_chatMessage));

        telemetryTrace.Verify(x =>
                x.AddException(
                    It.Is<Exception>(request =>
                        request.Message.Equals("Telemetry exception")),
                    It.IsAny<bool>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task SQSPublisher_MappingNotFound()
    {
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        await Assert.ThrowsAsync<MissingMessageTypeConfigurationException>(() => messagePublisher.PublishAsync(_chatMessage));
    }

    [Fact]
    public async Task SQSPublisher_InvalidMessage()
    {
        var serviceProvider = SetupSQSPublisherDIServices();

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        await Assert.ThrowsAsync<InvalidMessageException>(() => messagePublisher.PublishAsync<ChatMessage?>(null));
    }

    private IServiceProvider SetupSQSPublisherDIServices(string queueUrl = "endpoint")
    {
        var publisherConfiguration = new SQSPublisherConfiguration(queueUrl);
        var publisherMapping = new PublisherMapping(typeof(ChatMessage), publisherConfiguration, PublisherTargetType.SQS_PUBLISHER);

        _messageConfiguration.Setup(x => x.GetPublisherMapping(typeof(ChatMessage))).Returns(publisherMapping);

        var services = new ServiceCollection();
        services.AddSingleton<IAmazonSQS>(_sqsClient.Object);
        services.AddSingleton<ILogger<ISQSPublisher>>(_sqsPublisherLogger.Object);
        services.AddSingleton<IMessageConfiguration>(_messageConfiguration.Object);
        services.AddSingleton<IEnvelopeSerializer>(_envelopeSerializer.Object);
        services.AddSingleton<IAWSClientProvider, AWSClientProvider>();
        services.AddSingleton<ITelemetryFactory, DefaultTelemetryFactory>();

        return services.BuildServiceProvider();
    }

    private ISQSPublisher SetupSQSPublisher(IServiceProvider serviceProvider)
    {
        return new SQSPublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _sqsPublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            (ITelemetryFactory)serviceProvider.GetService(typeof(ITelemetryFactory))!
        );
    }

    [Fact]
    public async Task SQSPublisher_UnsupportedPublisher()
    {
        var serviceProvider = SetupSQSPublisherDIServices();

        var publisherMapping = new PublisherMapping(typeof(ChatMessage), null!, "NEW_PUBLISHER");
        _messageConfiguration.Setup(x => x.GetPublisherMapping(typeof(ChatMessage))).Returns(publisherMapping);

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        await Assert.ThrowsAsync<UnsupportedPublisherException>(() => messagePublisher.PublishAsync(_chatMessage));
    }

    /// <summary>
    /// Asserts that we can override the QueueURL for a specific message
    /// </summary>
    [Fact]
    public async Task SQSPublisher_MessageSpecificQueueUrl()
    {
        var serviceProvider = SetupSQSPublisherDIServices();
        var messagePublisher = SetupSQSPublisher(serviceProvider);
        _sqsClient.Setup(x =>
            x.SendMessageAsync(
                It.Is<SendMessageRequest>(request =>
                    request.QueueUrl.Equals("overrideEndpoint")),
                It.IsAny<CancellationToken>())).ReturnsAsync(new SendMessageResponse()
        {
            MessageId = "MessageId"
        });

        var sendResult = await messagePublisher.SendAsync(_chatMessage,
            new SQSOptions
            {
                QueueUrl = "overrideEndpoint"
            });

        // Assert we used the override endpoint specified above
        _sqsClient.Verify(x =>
                x.SendMessageAsync(
                    It.Is<SendMessageRequest>(request =>
                        request.QueueUrl.Equals("overrideEndpoint")),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));

        // And not the endpoint configured for this message type via SetupSQSPublisherDIServices
        _sqsClient.VerifyNoOtherCalls();
        Assert.Equal("MessageId", sendResult.MessageId);
    }

    /// <summary>
    /// Asserts that we can override the SQS client for a specific message
    /// </summary>
    [Fact]
    public async Task SQSPublisher_OverrideClient()
    {
        var serviceProvider = SetupSQSPublisherDIServices();
        var messagePublisher = SetupSQSPublisher(serviceProvider);

        var overrideSQSClient = new Mock<IAmazonSQS>();
        overrideSQSClient.Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SendMessageResponse()
        {
            MessageId = "MessageId"
        });

        var sendResult = await messagePublisher.SendAsync(_chatMessage,
            new SQSOptions
            {
                OverrideClient = overrideSQSClient.Object
            });

        // Assert that the override client was invoked
        overrideSQSClient.Verify(x =>
                x.SendMessageAsync(
                    It.IsAny<SendMessageRequest>(),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));

        // And not the default client
        _sqsClient.VerifyNoOtherCalls();

        Assert.Equal("MessageId", sendResult.MessageId);
    }

    /// <summary>
    /// Asserts that the expected exception is thrown with the queueURL is specified on neither
    /// the configuration nor the message-specific override
    /// </summary>
    [Fact]
    public async Task SQSPublisher_NoDestination_ThrowsException()
    {
        var serviceProvider = SetupSQSPublisherDIServices("");
        var messagePublisher = SetupSQSPublisher(serviceProvider);

        await Assert.ThrowsAsync<InvalidPublisherEndpointException>(() => messagePublisher.SendAsync(_chatMessage, new SQSOptions()));
    }

    private IServiceProvider SetupSNSPublisherDIServices(string topicArn = "endpoint")
    {
        var publisherConfiguration = new SNSPublisherConfiguration(topicArn);
        var publisherMapping = new PublisherMapping(typeof(ChatMessage), publisherConfiguration, PublisherTargetType.SNS_PUBLISHER);

        _messageConfiguration.Setup(x => x.GetPublisherMapping(typeof(ChatMessage))).Returns(publisherMapping);

        var services = new ServiceCollection();
        services.AddSingleton<IAmazonSimpleNotificationService>(_snsClient.Object);
        services.AddSingleton<ILogger<IMessagePublisher>>(_messagePublisherLogger.Object);
        services.AddSingleton<IMessageConfiguration>(_messageConfiguration.Object);
        services.AddSingleton<IEnvelopeSerializer>(_envelopeSerializer.Object);
        services.AddSingleton<IAWSClientProvider, AWSClientProvider>();
        services.AddSingleton<ITelemetryFactory, DefaultTelemetryFactory>();

        return services.BuildServiceProvider();
    }

    private ISNSPublisher SetupSNSPublisher(IServiceProvider serviceProvider)
    {
        return new SNSPublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _messagePublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            (ITelemetryFactory)serviceProvider.GetService(typeof(ITelemetryFactory))!
        );
    }

    [Fact]
    public async Task SNSPublisher_HappyPath()
    {
        var serviceProvider = SetupSNSPublisherDIServices();

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );
        _snsClient.Setup(x => x.PublishAsync(It.Is<PublishRequest>(request =>
                request.TopicArn.Equals("endpoint")),
            It.IsAny<CancellationToken>())).ReturnsAsync(new PublishResponse()
        {
            MessageId = "MessageId"
        });

        var publishResult = await messagePublisher.PublishAsync(_chatMessage);

        _snsClient.Verify(x =>
                x.PublishAsync(
                    It.Is<PublishRequest>(request =>
                        request.TopicArn.Equals("endpoint")),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));
        Assert.Equal("MessageId", publishResult.MessageId);
    }

    [Fact]
    public async Task SNSPublisher_TelemetryHappyPath()
    {
        var serviceProvider = SetupSNSPublisherDIServices();
        var telemetryFactory = new Mock<ITelemetryFactory>();
        var telemetryTrace = new Mock<ITelemetryTrace>();
        _snsClient.Setup(x =>
            x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(new PublishResponse()
        {
            MessageId = "MessageId"
        });

        telemetryFactory.Setup(x => x.Trace(It.IsAny<string>())).Returns(telemetryTrace.Object);
        telemetryTrace.Setup(x => x.AddMetadata(It.IsAny<string>(), It.IsAny<string>()));

        var messagePublisher = new SNSPublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _messagePublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            telemetryFactory.Object
        );

        await messagePublisher.PublishAsync(_chatMessage);

        telemetryFactory.Verify(x =>
                x.Trace(
                    It.Is<string>(request =>
                        request.Equals("Publish to AWS SNS"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.ObjectType)),
                    It.Is<string>(request =>
                        request.Equals("AWS.Messaging.UnitTests.Models.ChatMessage"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.MessageType)),
                    It.Is<string>(request =>
                        request.Equals("AWS.Messaging.UnitTests.Models.ChatMessage"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.TopicUrl)),
                    It.Is<string>(request =>
                        request.Equals("endpoint"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.MessageId)),
                    It.Is<string>(request =>
                        request.Equals("1234"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.RecordTelemetryContext(
                    It.IsAny<MessageEnvelope>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task SNSPublisher_TelemetryThrowsException()
    {
        var serviceProvider = SetupSNSPublisherDIServices();
        var telemetryFactory = new Mock<ITelemetryFactory>();
        var telemetryTrace = new Mock<ITelemetryTrace>();

        _snsClient.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("Telemetry exception"));
        telemetryFactory.Setup(x => x.Trace(It.IsAny<string>())).Returns(telemetryTrace.Object);
        telemetryTrace.Setup(x => x.AddMetadata(It.IsAny<string>(), It.IsAny<string>()));

        var messagePublisher = new SNSPublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _messagePublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            telemetryFactory.Object
        );

        await Assert.ThrowsAsync<Exception>(() => messagePublisher.PublishAsync(_chatMessage));

        telemetryTrace.Verify(x =>
                x.AddException(
                    It.Is<Exception>(request =>
                        request.Message.Equals("Telemetry exception")),
                    It.IsAny<bool>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task SNSPublisher_InvalidMessage()
    {
        var serviceProvider = SetupSNSPublisherDIServices();

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        await Assert.ThrowsAsync<InvalidMessageException>(() => messagePublisher.PublishAsync<ChatMessage?>(null));
    }

    /// <summary>
    /// Asserts that we can override the topic ARN for a specific message
    /// </summary>
    [Fact]
    public async Task SNSPublisher_MessageSpecificTopicArn()
    {
        var serviceProvider = SetupSNSPublisherDIServices();
        var messagePublisher = SetupSNSPublisher(serviceProvider);
        _snsClient.Setup(x =>
            x.PublishAsync(
                It.Is<PublishRequest>(request =>
                    request.TopicArn.Equals("overrideTopicArn")),
                It.IsAny<CancellationToken>())).ReturnsAsync(new PublishResponse()
        {
            MessageId = "MessageId"
        });

        var publishResponse = await messagePublisher.PublishAsync(_chatMessage,
            new SNSOptions
            {
                TopicArn = "overrideTopicArn"
            });

        // Assert we used the override topic arn specified above
        _snsClient.Verify(x =>
                x.PublishAsync(
                    It.Is<PublishRequest>(request =>
                        request.TopicArn.Equals("overrideTopicArn")),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));

        // And not the topic arn configured for this message type via SetupSNSPublisherDIServices
        _snsClient.VerifyNoOtherCalls();
        Assert.Equal("MessageId", publishResponse.MessageId);
    }

    /// <summary>
    /// Asserts that we can override the SNS client for a specific message
    /// </summary>
    [Fact]
    public async Task SNSPublisher_OverrideClient()
    {
        var serviceProvider = SetupSNSPublisherDIServices();
        var messagePublisher = SetupSNSPublisher(serviceProvider);

        var overrideSNSClient = new Mock<IAmazonSimpleNotificationService>();
        overrideSNSClient.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PublishResponse());

        await messagePublisher.PublishAsync(_chatMessage,
            new SNSOptions
            {
                OverrideClient = overrideSNSClient.Object
            });

        // Assert that the override client was invoked
        overrideSNSClient.Verify(x =>
                x.PublishAsync(
                    It.IsAny<PublishRequest>(),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));

        // And not the default client
        _snsClient.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Asserts that the expected exception is thrown with the topicArn is specified on neither
    /// the configuration nor the message-specific override
    /// </summary>
    [Fact]
    public async Task SNSPublisher_NoDestination_ThrowsException()
    {
        var serviceProvider = SetupSNSPublisherDIServices("");
        var messagePublisher = SetupSNSPublisher(serviceProvider);

        await Assert.ThrowsAsync<InvalidPublisherEndpointException>(() => messagePublisher.PublishAsync(_chatMessage, new SNSOptions()));
    }

    private IServiceProvider SetupEventBridgePublisherDIServices(string eventBusName, string? endpointID = null)
    {
        var publisherConfiguration = new EventBridgePublisherConfiguration(eventBusName)
        {
            EndpointID = endpointID
        };

        var publisherMapping = new PublisherMapping(typeof(ChatMessage), publisherConfiguration, PublisherTargetType.EVENTBRIDGE_PUBLISHER);

        _messageConfiguration.Setup(x => x.GetPublisherMapping(typeof(ChatMessage))).Returns(publisherMapping);

        var services = new ServiceCollection();
        services.AddSingleton<IAmazonEventBridge>(_eventBridgeClient.Object);
        services.AddSingleton<ILogger<IMessagePublisher>>(_messagePublisherLogger.Object);
        services.AddSingleton<IMessageConfiguration>(_messageConfiguration.Object);
        services.AddSingleton<IEnvelopeSerializer>(_envelopeSerializer.Object);
        services.AddSingleton<IAWSClientProvider, AWSClientProvider>();
        services.AddSingleton<ITelemetryFactory, DefaultTelemetryFactory>();

        return services.BuildServiceProvider();
    }

    private IEventBridgePublisher SetupEventBridgePublisher(IServiceProvider serviceProvider)
    {
        return new EventBridgePublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _messagePublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            (ITelemetryFactory)serviceProvider.GetService(typeof(ITelemetryFactory))!
        );
    }

    [Fact]
    public async Task EventBridgePublisher_HappyPath()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("event-bus-123");
        _eventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutEventsResponse
        {
            Entries = new List<PutEventsResultEntry>
            {
                new()
                {
                    EventId = "ReturnedEventId"
                }
            }
        });

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        var publishResponse = await messagePublisher.PublishAsync(_chatMessage);

        _eventBridgeClient.Verify(x =>
                x.PutEventsAsync(
                    It.Is<PutEventsRequest>(request =>
                        request.Entries[0].EventBusName.Equals("event-bus-123") && string.IsNullOrEmpty(request.EndpointId)
                                                                                && request.Entries[0].DetailType.Equals("AWS.Messaging.UnitTests.Models.ChatMessage") && request.Entries[0].Source.Equals("/aws/messaging/unittest")),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));

        Assert.Equal("ReturnedEventId", publishResponse.MessageId);
    }

    [Fact]
    public async Task EventBridgePublisher_UnhappyPath()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("event-bus-123");
        _eventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutEventsResponse
        {
            Entries = new List<PutEventsResultEntry>
            {
                new()
                {
                    ErrorMessage = "ErrorMessage",
                    ErrorCode = "ErrorCode"
                }
            }
        });

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        var publishResponse = Assert.ThrowsAsync<FailedToPublishException>(async () => await messagePublisher.PublishAsync(_chatMessage));

        _eventBridgeClient.Verify(x =>
                x.PutEventsAsync(
                    It.Is<PutEventsRequest>(request =>
                        request.Entries[0].EventBusName.Equals("event-bus-123") && string.IsNullOrEmpty(request.EndpointId)
                                                                                && request.Entries[0].DetailType.Equals("AWS.Messaging.UnitTests.Models.ChatMessage") && request.Entries[0].Source.Equals("/aws/messaging/unittest")),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));

        Assert.Equal("Message failed to publish.", publishResponse.Result.Message);
        Assert.Equal("ErrorMessage", publishResponse.Result.InnerException.Message);
        Assert.Equal("ErrorCode", ((EventBridgePutEventsException)publishResponse.Result.InnerException).ErrorCode);
    }

    [Fact]
    public async Task EventBridgePublisher_TelemetryHappyPath()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("event-bus-123");
        var telemetryFactory = new Mock<ITelemetryFactory>();
        var telemetryTrace = new Mock<ITelemetryTrace>();

        telemetryFactory.Setup(x => x.Trace(It.IsAny<string>())).Returns(telemetryTrace.Object);
        telemetryTrace.Setup(x => x.AddMetadata(It.IsAny<string>(), It.IsAny<string>()));

        var messagePublisher = new EventBridgePublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _messagePublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            telemetryFactory.Object
        );
        _eventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutEventsResponse
        {
            Entries = new List<PutEventsResultEntry>
            {
                new()
                {
                    EventId = "ReturnedEventId"
                }
            }
        });

        await messagePublisher.PublishAsync(_chatMessage);

        telemetryFactory.Verify(x =>
                x.Trace(
                    It.Is<string>(request =>
                        request.Equals("Publish to AWS EventBridge"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.ObjectType)),
                    It.Is<string>(request =>
                        request.Equals("AWS.Messaging.UnitTests.Models.ChatMessage"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.MessageType)),
                    It.Is<string>(request =>
                        request.Equals("AWS.Messaging.UnitTests.Models.ChatMessage"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.EventBusName)),
                    It.Is<string>(request =>
                        request.Equals("event-bus-123"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.AddMetadata(
                    It.Is<string>(request =>
                        request.Equals(TelemetryKeys.MessageId)),
                    It.Is<string>(request =>
                        request.Equals("1234"))),
            Times.Exactly(1));

        telemetryTrace.Verify(x =>
                x.RecordTelemetryContext(
                    It.IsAny<MessageEnvelope>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task EventBridgePublisher_TelemetryThrowsException()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("event-bus-123");
        var telemetryFactory = new Mock<ITelemetryFactory>();
        var telemetryTrace = new Mock<ITelemetryTrace>();

        _eventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("Telemetry exception"));
        telemetryFactory.Setup(x => x.Trace(It.IsAny<string>())).Returns(telemetryTrace.Object);
        telemetryTrace.Setup(x => x.AddMetadata(It.IsAny<string>(), It.IsAny<string>()));

        var messagePublisher = new EventBridgePublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _messagePublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            telemetryFactory.Object
        );

        await Assert.ThrowsAsync<Exception>(() => messagePublisher.PublishAsync(_chatMessage));

        telemetryTrace.Verify(x =>
                x.AddException(
                    It.Is<Exception>(request =>
                        request.Message.Equals("Telemetry exception")),
                    It.IsAny<bool>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task EventBridgePublisher_GlobalEP()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("event-bus-123", "endpoint.123");
        _eventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutEventsResponse
        {
            Entries = new List<PutEventsResultEntry>
            {
                new()
                {
                    EventId = "ReturnedEventId"
                }
            }
        });

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        await messagePublisher.PublishAsync(_chatMessage);

        _eventBridgeClient.Verify(x =>
                x.PutEventsAsync(
                    It.Is<PutEventsRequest>(request =>
                        request.Entries[0].EventBusName.Equals("event-bus-123") && request.EndpointId.Equals("endpoint.123")
                                                                                && request.Entries[0].DetailType.Equals("AWS.Messaging.UnitTests.Models.ChatMessage") && request.Entries[0].Source.Equals("/aws/messaging/unittest")),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task EventBridgePublisher_OptionSource()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("event-bus-123");
        var messagePublisher = SetupEventBridgePublisher(serviceProvider);

        _eventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutEventsResponse
        {
            Entries = new List<PutEventsResultEntry>
            {
                new()
                {
                    EventId = "ReturnedEventId"
                }
            }
        });

        await messagePublisher.PublishAsync(_chatMessage,
            new EventBridgeOptions
            {
                Source = "/aws/custom"
            });

        _eventBridgeClient.Verify(x =>
                x.PutEventsAsync(
                    It.Is<PutEventsRequest>(request =>
                        request.Entries[0].EventBusName.Equals("event-bus-123") && string.IsNullOrEmpty(request.EndpointId)
                                                                                && request.Entries[0].DetailType.Equals("AWS.Messaging.UnitTests.Models.ChatMessage") && request.Entries[0].Source.Equals("/aws/custom")),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task EventBridgePublisher_SetOptions()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("event-bus-123");
        var messagePublisher = SetupEventBridgePublisher(serviceProvider);

        _eventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutEventsResponse
        {
            Entries = new List<PutEventsResultEntry>
            {
                new()
                {
                    EventId = "ReturnedEventId"
                }
            }
        });
        DateTimeOffset dateTimeOffset = new DateTimeOffset(2015, 2, 17, 0, 0, 0, TimeSpan.Zero);

        await messagePublisher.PublishAsync(_chatMessage,
            new EventBridgeOptions
            {
                TraceHeader = "trace-header1",
                Time = dateTimeOffset
            });

        _eventBridgeClient.Verify(x =>
                x.PutEventsAsync(
                    It.Is<PutEventsRequest>(request =>
                        request.Entries[0].EventBusName.Equals("event-bus-123") && string.IsNullOrEmpty(request.EndpointId)
                                                                                && request.Entries[0].TraceHeader.Equals("trace-header1") && request.Entries[0].Time.Year == dateTimeOffset.Year),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));
    }

    [Fact]
    public async Task EventBridgePublisher_InvalidMessage()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("event-bus-123");

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        await Assert.ThrowsAsync<InvalidMessageException>(() => messagePublisher.PublishAsync<ChatMessage?>(null));
    }

    /// <summary>
    /// Asserts that we can override the EventBridge destination for a specific message
    /// </summary>
    [Fact]
    public async Task EventBridgePublisher_MessageSpecificTopicArn()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("defaultBus", "defaultEndpoint");
        var messagePublisher = SetupEventBridgePublisher(serviceProvider);
        _eventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutEventsResponse
        {
            Entries = new List<PutEventsResultEntry>
            {
                new()
                {
                    EventId = "ReturnedEventId"
                }
            }
        });
        await messagePublisher.PublishAsync(_chatMessage,
            new EventBridgeOptions
            {
                EventBusName = "overrideBus",
                EndpointID = "overrideEndpoint"
            });

        // Assert we used the event bus and endpointspecified above
        _eventBridgeClient.Verify(x =>
                x.PutEventsAsync(
                    It.Is<PutEventsRequest>(request =>
                        request.Entries[0].EventBusName.Equals("overrideBus") &&
                        request.EndpointId == "overrideEndpoint"),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));

        // And not the desination configured for this message type via SetupEventBridgePublisherDIServices
        _eventBridgeClient.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Asserts that we can override the SNS client for a specific message
    /// </summary>
    [Fact]
    public async Task EventBridgePublisher_OverrideClient()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("defaultBus", "defaultEndpoint");
        var messagePublisher = SetupEventBridgePublisher(serviceProvider);

        var overrideEventBridgeClient = new Mock<IAmazonEventBridge>();
        overrideEventBridgeClient.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PutEventsResponse
        {
            Entries = new List<PutEventsResultEntry>
            {
                new()
                {
                    EventId = "ReturnedEventId"
                }
            }
        });
        await messagePublisher.PublishAsync(_chatMessage,
            new EventBridgeOptions
            {
                OverrideClient = overrideEventBridgeClient.Object
            });

        // Assert that the override client was invoked
        overrideEventBridgeClient.Verify(x =>
                x.PutEventsAsync(
                    It.IsAny<PutEventsRequest>(),
                    It.IsAny<CancellationToken>()),
            Times.Exactly(1));

        // And not the default client
        _eventBridgeClient.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Asserts that the expected exception is thrown with the event bus is specified on neither
    /// the configuration nor the message-specific override
    /// </summary>
    [Fact]
    public async Task EventBridgePublisher_NoDestination_ThrowsException()
    {
        var serviceProvider = SetupEventBridgePublisherDIServices("");
        var messagePublisher = SetupEventBridgePublisher(serviceProvider);

        await Assert.ThrowsAsync<InvalidPublisherEndpointException>(() => messagePublisher.PublishAsync(_chatMessage, new EventBridgeOptions()));
    }

    [Fact]
    public async Task PublishToFifoQueue_WithoutMessageGroupId_ThrowsException()
    {
        var serviceProvider = SetupSQSPublisherDIServices("endpoint.fifo");

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        var sqsMessagePublisher = new SQSPublisher(
            (IAWSClientProvider)serviceProvider.GetService(typeof(IAWSClientProvider))!,
            _sqsPublisherLogger.Object,
            _messageConfiguration.Object,
            _envelopeSerializer.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        await Assert.ThrowsAsync<InvalidFifoPublishingRequestException>(() => messagePublisher.PublishAsync<ChatMessage?>(new ChatMessage()));
        await Assert.ThrowsAsync<InvalidFifoPublishingRequestException>(() => sqsMessagePublisher.SendAsync<ChatMessage?>(new ChatMessage(), new SQSOptions()));
    }

    [Fact]
    public async Task PublishToFifoTopic_WithoutMessageGroupId_ThrowsException()
    {
        var serviceProvider = SetupSNSPublisherDIServices("endpoint.fifo");

        var messagePublisher = new MessageRoutingPublisher(
            serviceProvider,
            _messageConfiguration.Object,
            _messagePublisherLogger.Object,
            new DefaultTelemetryFactory(serviceProvider)
        );

        var snsMessagePublisher = SetupSNSPublisher(serviceProvider);

        await Assert.ThrowsAsync<InvalidFifoPublishingRequestException>(() => messagePublisher.PublishAsync<ChatMessage?>(new ChatMessage()));
        await Assert.ThrowsAsync<InvalidFifoPublishingRequestException>(() => snsMessagePublisher.PublishAsync<ChatMessage?>(new ChatMessage(), new SNSOptions()));
    }
}
