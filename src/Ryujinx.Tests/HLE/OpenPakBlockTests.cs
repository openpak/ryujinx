using NUnit.Framework;
using Ryujinx.OpenPak;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// Block and unblock (30400–30403) against the friends contract §A.8: the POST body, the
    /// re-syncs a success triggers, the remaps, and the local removal a 404 unblock makes.
    /// </summary>
    [NonParallelizable]
    public class OpenPakBlockTests
    {
        private const string Me = "0123456789abcdef";
        private const ulong Target = 0x11cf3b1423e1183f;

        private readonly List<(HttpMethod Method, string Url, string Body)> _sent = [];
        private readonly Dictionary<(string Method, string Fragment), OpenPakBaas.Reply> _replies = [];

        [SetUp]
        public void Attach()
        {
            _sent.Clear();
            _replies.Clear();

            OpenPakBaas.Attach(Me, (method, url, _, body, _) =>
            {
                _sent.Add((method, url, body));

                foreach (((string verb, string fragment), OpenPakBaas.Reply reply) in _replies)
                {
                    if (method.Method == verb && url.Contains(fragment))
                    {
                        return Task.FromResult(reply);
                    }
                }

                return Task.FromResult(new OpenPakBaas.Reply(200, """{"items":[]}"""));
            });
        }

        [TearDown]
        public void Detach() => OpenPakBaas.Detach();

        [Test]
        public void TheBodyIsTheModules()
        {
            using JsonDocument plain = JsonDocument.Parse(OpenPakBaas.BlockBody(Target, "BAD_FRIEND", null));

            Assert.That(plain.RootElement.GetProperty("targetUserId").GetString(), Is.EqualTo("11cf3b1423e1183f"));
            Assert.That(plain.RootElement.GetProperty("extras").GetProperty("self").GetProperty("reason").GetString(),
                Is.EqualTo("BAD_FRIEND"));
            Assert.That(plain.RootElement.GetProperty("extras").GetProperty("self").EnumerateObject().Count(), Is.EqualTo(1));

            BaasRoute route = new(0x0100a5a020d5e000, 1, 0x0100a5a020d5e000, null, "Leia", "en-US", null, null);

            using JsonDocument inApp = JsonDocument.Parse(OpenPakBaas.BlockBody(Target, "IN_APP", route));
            JsonElement self = inApp.RootElement.GetProperty("extras").GetProperty("self");

            Assert.That(self.GetProperty("reason").GetString(), Is.EqualTo("IN_APP"));
            Assert.That(self.GetProperty("route:appInfo:appId").GetString(), Is.EqualTo("0100a5a020d5e000"));
            Assert.That(self.GetProperty("route:appInfo:acdIndex").GetInt32(), Is.EqualTo(1));
            Assert.That(self.GetProperty("route:name").GetString(), Is.EqualTo("Leia"));
            Assert.That(self.GetProperty("route:name:language").GetString(), Is.EqualTo("en-US"));
        }

        [Test]
        public async Task ABlockPostsThenResyncsBlocksAndFriends()
        {
            _replies[("GET", "/blocks?")] = new OpenPakBaas.Reply(200,
                """{"items":[{"targetUserId":"11cf3b1423e1183f","targetUser":{"nickname":"Leia","thumbnailUrl":""},"extras":{"self":{"reason":"BAD_FRIEND"}}}]}""");

            bool changed = false;
            void OnChanged() => changed = true;

            OpenPakBaas.BlockListChanged += OnChanged;

            try
            {
                Assert.That(await OpenPakBaas.BlockUserAsync(Target, 2, null, CancellationToken.None), Is.EqualTo(OpenPakBaas.Ok));
            }
            finally
            {
                OpenPakBaas.BlockListChanged -= OnChanged;
            }

            Assert.That(_sent[0].Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(_sent[0].Url, Does.EndWith($"/1.0.0/users/{Me}/blocks"));
            Assert.That(_sent[1].Url, Does.Contain("/blocks?count=100"));
            Assert.That(_sent[2].Url, Does.Contain("/friends?count=300"));
            Assert.That(changed, Is.True);
            Assert.That(OpenPakBaas.Blocks.Single().Reason, Is.EqualTo(2));
        }

        [Test]
        public async Task ARefusedBlockIsRemappedAndNotKept()
        {
            _replies[("POST", "/blocks")] = new OpenPakBaas.Reply(409,
                """{"status":409,"errorCode":"resource_already_exists"}""");

            Assert.That(await OpenPakBaas.BlockUserAsync(Target, 1, null, CancellationToken.None), Is.EqualTo(2701));
            Assert.That(OpenPakBaas.Blocks, Is.Empty);
        }

        [Test]
        public async Task AReasonOutsideTheTableIsRefusedLocally()
        {
            Assert.That(await OpenPakBaas.BlockUserAsync(Target, 9, null, CancellationToken.None), Is.EqualTo(OpenPakBaas.InvalidArgument));
            Assert.That(_sent, Is.Empty);
        }

        [Test]
        public async Task UnblockingOneTheServerDoesNotKnowIs2711()
        {
            _replies[("DELETE", "/blocks/")] = new OpenPakBaas.Reply(404,
                """{"status":404,"errorCode":"resource_is_not_found"}""");

            Assert.That(await OpenPakBaas.UnblockUserAsync(Target, CancellationToken.None), Is.EqualTo(2711));
            Assert.That(_sent.Single().Url, Does.EndWith($"/blocks/{Target:x16}"));
        }

        [Test]
        public async Task AnUnblockResyncs()
        {
            Assert.That(await OpenPakBaas.UnblockUserAsync(Target, CancellationToken.None), Is.EqualTo(OpenPakBaas.Ok));
            Assert.That(_sent.Select(sent => sent.Method), Is.EqualTo(new[] { HttpMethod.Delete, HttpMethod.Get, HttpMethod.Get }));
        }
    }
}
