﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Activation;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Sagas;
using Rebus.Tests.Extensions;
using Rebus.Transport;
using Rebus.Transport.InMem;

namespace Rebus.Tests.Integration
{
    [TestFixture]
    public class TestIdempotentSagas : FixtureBase
    {
        BuiltinHandlerActivator _activator;
        IBus _bus;
        ConcurrentDictionary<Guid, ISagaData> _persistentSagaData;

        protected override void SetUp()
        {
            _activator = new BuiltinHandlerActivator();

            _persistentSagaData = new ConcurrentDictionary<Guid, ISagaData>();

            _bus = Configure.With(_activator)
                .Logging( l => l.Console(LogLevel.Warn))
                .Transport(t =>
                {
                    t.UseInMemoryTransport(new InMemNetwork(), "bimse");
                    t.Decorate(c => new IntroducerOfTransportInstability(c.Get<ITransport>()));
                })
                .Sagas(s =>
                {
                    s.Decorate(c => new SagaStorageTap(c.Get<ISagaStorage>(), _persistentSagaData));
                })
                .Start();
        }

        [Test]
        public async Task ItWorks()
        {
            var allMessagesReceived = new ManualResetEvent(false);

            _activator.Register(() => new MyIdempotentSaga(allMessagesReceived));

            const int total = 30;

            var messagesToSend = Enumerable
                .Range(0, total)
                .Select(id => new MyMessage
                {
                    CorrelationId = "hej",
                    Id = id,
                    Total = total
                })
                .ToList();

            await Task.WhenAll(messagesToSend.Select(message => _bus.SendLocal(message)));

            allMessagesReceived.WaitOrDie(TimeSpan.FromSeconds(5));

            await Task.Delay(300);

            var allIdempotentSagaData = _persistentSagaData.Values
                .OfType<MyIdempotentSagaData>()
                .ToList();

            Assert.That(allIdempotentSagaData.Count, Is.EqualTo(1));

            var instance = allIdempotentSagaData.First();

            Assert.That(instance.CountPerId.Count, Is.EqualTo(total));

            Assert.That(instance.CountPerId.All(c => c.Value == 1), Is.True, 
                "Not all counts were exactly one: {0} - this is a sign that the saga was not truly idempotent, as the redelivery should have been caught!",
                string.Join(", ", instance.CountPerId.Where(c => c.Value > 1).Select(c => string.Format("{0}: {1}", c.Key, c.Value))));
        }

        class SagaStorageTap : ISagaStorage
        {
            readonly ISagaStorage _innerSagaStorage;
            readonly ConcurrentDictionary<Guid, ISagaData> _persistentSagaData;

            public SagaStorageTap(ISagaStorage innerSagaStorage, ConcurrentDictionary<Guid, ISagaData> persistentSagaData)
            {
                _innerSagaStorage = innerSagaStorage;
                _persistentSagaData = persistentSagaData;
            }

            public async Task<ISagaData> Find(Type sagaDataType, string propertyName, object propertyValue)
            {
                return await _innerSagaStorage.Find(sagaDataType, propertyName, propertyValue);
            }

            public async Task Insert(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
            {
                await _innerSagaStorage.Insert(sagaData, correlationProperties);

                _persistentSagaData[sagaData.Id] = sagaData;
            }

            public async Task Update(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
            {
                await _innerSagaStorage.Update(sagaData, correlationProperties);

                _persistentSagaData[sagaData.Id] = sagaData;
            }

            public async Task Delete(ISagaData sagaData)
            {
                await _innerSagaStorage.Delete(sagaData);

                ISagaData _;
                _persistentSagaData.TryRemove(sagaData.Id, out _);
            }
        }

        class MyIdempotentSaga : IdempotentSaga<MyIdempotentSagaData>, IAmInitiatedBy<MyMessage>
        {
            readonly ManualResetEvent _allMessagesReceived;

            public MyIdempotentSaga(ManualResetEvent allMessagesReceived)
            {
                _allMessagesReceived = allMessagesReceived;
            }

            protected override void CorrelateMessages(ICorrelationConfig<MyIdempotentSagaData> config)
            {
                config.Correlate<MyMessage>(m => m.CorrelationId, d => d.CorrelationId);
            }

            public async Task Handle(MyMessage message)
            {
                Data.CorrelationId = message.CorrelationId;

                if (!Data.CountPerId.ContainsKey(message.Id))
                {
                    Data.CountPerId[message.Id] = 0;
                }

                Data.CountPerId[message.Id]++;

                if (Data.CountPerId.Count == message.Total)
                {
                    _allMessagesReceived.Set();
                }
            }
        }

        class MyIdempotentSagaData : ISagaData
        {
            public MyIdempotentSagaData()
            {
                CountPerId = new Dictionary<int, int>();
            }
            public Guid Id { get; set; }
            public int Revision { get; set; }
            public string CorrelationId { get; set; }
            public Dictionary<int, int> CountPerId { get; set; }
        }

        class MyMessage
        {
            public string CorrelationId { get; set; }
            public int Id { get; set; }
            public int Total { get; set; }
            public override string ToString()
            {
                return string.Format("MyMessage {0}/{1}", Id, Total);
            }
        }

        class IntroducerOfTransportInstability : ITransport
        {
            readonly Random _random = new Random(DateTime.Now.GetHashCode());
            readonly ITransport _innerTransport;

            public IntroducerOfTransportInstability(ITransport innerTransport)
            {
                _innerTransport = innerTransport;
            }

            public void CreateQueue(string address)
            {
                _innerTransport.CreateQueue(address);
            }

            public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
            {
                await _innerTransport.Send(destinationAddress, message, context);
            }

            public async Task<TransportMessage> Receive(ITransactionContext context)
            {
                var transportMessage = await _innerTransport.Receive(context);
                if (transportMessage == null) return null;

                if (GetNext() == 0)
                {
                    context.OnCommitted(async () =>
                    {
                        throw new Exception("oh noes!!!!!");
                    });
                }

                return transportMessage;
            }

            int GetNext()
            {
                lock (this)
                {
                    return _random.Next(5);
                }
            }

            public string Address
            {
                get { return _innerTransport.Address; }
            }
        }
    }
}