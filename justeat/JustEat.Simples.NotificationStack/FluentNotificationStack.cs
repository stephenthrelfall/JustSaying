using System;
using Amazon;
using JustSaying.AwsTools;
using JustSaying.AwsTools.QueueCreation;
using JustSaying.Messaging.MessageProcessingStrategies;
using JustSaying.Messaging;
using JustSaying.Messaging.MessageHandling;
using JustSaying.Messaging.MessageSerialisation;
using JustSaying.Messaging.Messages;
using JustSaying.Messaging.Monitoring;
using JustSaying.Stack.Lookups;
using JustSaying.Stack.Monitoring;
using JustEat.StatsD;
using NLog;

namespace JustSaying.Stack
{
    /// <summary>
    /// This is not the perfect shining example of a fluent API YET!
    /// Intended usage:
    /// 1. Call Register()
    /// 2. Set subscribers - WithSqsTopicSubscriber() / WithSnsTopicSubscriber() etc
    /// 3. Set Handlers - WithTopicMessageHandler()
    /// </summary>
    public class FluentNotificationStack : IFluentMonitoring, IFluentSubscription
    {
        private static readonly Logger Log = LogManager.GetLogger("JustSaying");
        private readonly IVerifyAmazonQueues _amazonQueueCreator;
        private readonly INotificationStack _stack;
        private string _currnetTopic;

        public static string DefaultEndpoint
        {
            get { return RegionEndpoint.EUWest1.SystemName; }
        }

        private FluentNotificationStack(INotificationStack stack, IVerifyAmazonQueues queueCreator)
        {
            _stack = stack;
            _amazonQueueCreator = queueCreator;
        }

        public static IFluentMonitoring Register(Action<INotificationStackConfiguration> configuration)
        {
            var config = new MessagingConfig();
            configuration.Invoke(config);

            if (string.IsNullOrWhiteSpace(config.Environment))
                throw new ArgumentNullException("config.Environment", "Cannot have a blank entry for config.Environment");

            if (string.IsNullOrWhiteSpace(config.Tenant))
                throw new ArgumentNullException("config.Tenant", "Cannot have a blank entry for config.Tenant");

            if (string.IsNullOrWhiteSpace(config.Component))
                throw new ArgumentNullException("config.Component", "Cannot have a blank entry for config.Component");
            
            if (string.IsNullOrWhiteSpace(config.Region))
            {
                config.Region = RegionEndpoint.EUWest1.SystemName;
                Log.Info("No Region was specified, using {0} by default.", config.Region);
            }

            return new FluentNotificationStack(new NotificationStack(config, new MessageSerialisationRegister()), new AmazonQueueCreator());
        }

        /// <summary>
        /// Subscribe to a topic using SQS.
        /// </summary>
        /// <param name="topic">Topic to listen in on</param>
        /// <param name="messageRetentionSeconds">Time messages should be kept in this queue</param>
        /// <param name="visibilityTimeoutSeconds">Seconds message should be invisible to other other receiving components</param>
        /// <param name="instancePosition">Optional instance position as tagged by paas tools in AWS. Using this will cause the message to get handled by EACH instance in your cluster</param>
        /// <param name="onError">Optional error handler. Use this param to inject custom error handling from within the consuming application</param>
        /// <param name="maxAllowedMessagesInFlight">Configures the stack to use the Throttled handling strategy, configured to this level of concurrent messages in flight</param>
        /// <param name="messageProcessingStrategy">Hook to supply your own IMessageProcessingStrategy</param>
        /// <returns></returns>
        public IFluentSubscription WithSqsTopicSubscriber(string topic, int messageRetentionSeconds, int visibilityTimeoutSeconds = 30, int? instancePosition = null, Action<Exception> onError = null, int? maxAllowedMessagesInFlight = null, IMessageProcessingStrategy messageProcessingStrategy = null)
        {
            return WithSqsTopicSubscriber(cf =>
            {
                cf.Topic = topic;
                cf.MessageRetentionSeconds = messageRetentionSeconds;
                cf.VisibilityTimeoutSeconds = visibilityTimeoutSeconds;
                cf.InstancePosition = instancePosition;
                cf.OnError = onError;
                cf.MaxAllowedMessagesInFlight = maxAllowedMessagesInFlight;
                cf.MessageProcessingStrategy = messageProcessingStrategy;
            });
        }

        public IFluentSubscription WithSqsTopicSubscriber(Action<SqsConfiguration> confBuilder)
        {
            var config = new SqsConfiguration();
            confBuilder(config);
            
            var subscriptionEndpointProvider = new SqsSubscribtionEndpointProvider(_stack.Config);
            var publishEndpointProvider = new SnsPublishEndpointProvider(_stack.Config);

            config.QueueName = config.InstancePosition.HasValue
                                ? subscriptionEndpointProvider.GetLocationName(_stack.Config.Component, config.Topic, config.InstancePosition.Value)
                                : subscriptionEndpointProvider.GetLocationName(_stack.Config.Component, config.Topic);
            config.PublishEndpoint = publishEndpointProvider.GetLocationName(config.Topic);
            config.Validate();
            var queue = _amazonQueueCreator.VerifyOrCreateQueue(_stack.Config.Region, _stack.SerialisationRegister, config);

            var sqsSubscriptionListener = new SqsNotificationListener(queue, _stack.SerialisationRegister, new NullMessageFootprintStore(), _stack.Monitor, config.OnError);
            _stack.AddNotificationTopicSubscriber(config.Topic, sqsSubscriptionListener);
            
            if (config.MaxAllowedMessagesInFlight.HasValue)
                sqsSubscriptionListener.WithMaximumConcurrentLimitOnMessagesInFlightOf(config.MaxAllowedMessagesInFlight.Value);

            if (config.MessageProcessingStrategy != null)
                sqsSubscriptionListener.WithMessageProcessingStrategy(config.MessageProcessingStrategy);

            Log.Info(string.Format("Created SQS topic subscription - Component: {0}, Topic: {1}, QueueName: {2}", _stack.Config.Component, config.Topic, config.QueueName));
            _currnetTopic = config.Topic;

            return this;
        }

        public IFluentSubscription WithSqsTopicSubscriber(string topic, int messageRetentionSeconds, IMessageProcessingStrategy messageProcessingStrategy)
        {
            return WithSqsTopicSubscriber(topic, messageRetentionSeconds, 30, null, null, null,
                messageProcessingStrategy);
        }

        /// <summary>
        /// Register for publishing messages to SNS
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="topic"></param>
        /// <returns></returns>
        public IFluentNotificationStack WithSnsMessagePublisher<T>(string topic) where T : Message
        {
            Log.Info("Added publisher");

            var endpointProvider = new SnsPublishEndpointProvider(_stack.Config);
            var eventPublisher = new SnsTopicByName(
                endpointProvider.GetLocationName(topic),
                AWSClientFactory.CreateAmazonSimpleNotificationServiceClient(RegionEndpoint.GetBySystemName(_stack.Config.Region)),
                _stack.SerialisationRegister);

            if (!eventPublisher.Exists())
                eventPublisher.Create();

            _stack.SerialisationRegister.AddSerialiser<T>(new ServiceStackSerialiser<T>());
            _stack.AddMessagePublisher<T>(topic, eventPublisher);

            Log.Info(string.Format("Created SNS topic publisher - Component: {0}, Topic: {1}", _stack.Config.Component, topic));

            return this;
        }

        /// <summary>
        /// I'm done setting up. Fire up listening on this baby...
        /// </summary>
        public void StartListening()
        {
            _stack.Start();
            Log.Info("Started listening for messages: Component: " + _stack.Config.Component);
        }

        /// <summary>
        /// Gor graceful shutdown of all listening threads
        /// </summary>
        public void StopListening()
        {
            _stack.Stop();
            Log.Info("Stopped listening for messages: Component: " + _stack.Config.Component);
        }

        /// <summary>
        /// Publish a message to the stack.
        /// </summary>
        /// <param name="message"></param>
        public void Publish(Message message)
        {
            if (_stack == null)
                throw new InvalidOperationException("You must register for message publication before publishing a message");
            
            _stack.Publish(message);
        }

        /// <summary>
        /// States whether the stack is listening for messages (subscriptions are running)
        /// </summary>
        public bool Listening { get { return (_stack != null) && _stack.Listening; } }
        
        #region Implementation of IFluentSubscription

        /// <summary>
        /// Set message handlers for the given topic
        /// </summary>
        /// <typeparam name="T">Message type to be handled</typeparam>
        /// <param name="handler">Handler for the message type</param>
        /// <returns></returns>
        public IFluentSubscription WithMessageHandler<T>(IHandler<T> handler) where T : Message
        {
            _stack.SerialisationRegister.AddSerialiser<T>(new ServiceStackSerialiser<T>());
            _stack.AddMessageHandler(_currnetTopic, handler);

            Log.Info(string.Format("Added a message handler - Component: {0}, Topic: {1}, MessageType: {2}, HandlerName: {3}", _stack.Config.Component, _currnetTopic, typeof(T).Name, handler.GetType().Name));

            return this;
        }

        #endregion

        #region Implementation of IFluentMonitoring

        /// <summary>
        /// Provide your own monitoring implementation
        /// </summary>
        /// <param name="messageMonitor">Monitoring class to be used</param>
        /// <returns></returns>
        public IFluentNotificationStack WithMonitoring(IMessageMonitor messageMonitor)
        {
            _stack.Monitor = messageMonitor;
            return this;
        }

        /// <summary>
        /// Use the default JustEat StatsD Monitoring tooling
        /// </summary>
        /// <param name="publisher">The JustEat.StatsD publisher you use in your application (suggest immediate publisher)</param>
        /// <returns></returns>
        public IFluentNotificationStack WithStatsDMonitoring(IStatsDPublisher publisher)
        {
            _stack.Monitor = new StatsDMessageMonitor(publisher);
            return this;
        }

        #endregion
    }

    public interface IFluentNotificationStack : IMessagePublisher
    {
        IFluentNotificationStack WithSnsMessagePublisher<T>(string topic) where T : Message;

        IFluentSubscription WithSqsTopicSubscriber(string topic, int messageRetentionSeconds,
            int visibilityTimeoutSeconds = 30, int? instancePosition = null, Action<Exception> onError = null,
            int? maxAllowedMessagesInFlight = null, IMessageProcessingStrategy messageProcessingStrategy = null);
        IFluentSubscription WithSqsTopicSubscriber(string topic, int messageRetentionSeconds, IMessageProcessingStrategy messageProcessingStrategy);
        IFluentSubscription WithSqsTopicSubscriber(Action<SqsConfiguration> confBuilder);

        void StartListening();
        void StopListening();
        bool Listening { get; }
    }

    public interface IFluentMonitoring
    {
        IFluentNotificationStack WithMonitoring(IMessageMonitor messageMonitor);
        IFluentNotificationStack WithStatsDMonitoring(IStatsDPublisher publisher);
    }

    public interface IFluentSubscription : IFluentNotificationStack
    {
        IFluentSubscription WithMessageHandler<T>(IHandler<T> handler) where T : Message;
    }
}