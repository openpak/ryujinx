using NUnit.Framework;
using Ryujinx.OpenPak;
using System;
using System.Linq;
using System.Text.Json;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The friends module's reply parsers (<see cref="OpenPakBaas"/>), pinned against the
    /// friends contract: which fields are required, what types they must have, and what a
    /// missing one does. Unknown keys are ignored everywhere; a field of the wrong type
    /// counts as absent.
    /// </summary>
    public class OpenPakBaasTests
    {
        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        // ---- ids ----

        [Test]
        public void HexReadsTheModuleForms(
            [Values("11cf3b1423e1183f", "0x11cf3b1423e1183f", "0X11CF3B1423E1183F",
                "  11cf3b1423e1183f", "+11cf3b1423e1183f")] string text)
        {
            using JsonDocument document = JsonDocument.Parse($"\"{text}\"");

            Assert.That(OpenPakBaas.TryHex(document.RootElement, out ulong value), Is.True);
            Assert.That(value, Is.EqualTo(0x11cf3b1423e1183f));
        }

        [Test]
        public void HexRejectsWhatTheModuleRejects(
            [Values("-", "-1", "0x", "", "   ", "12345678901234567")] string text)
        {
            using JsonDocument document = JsonDocument.Parse($"\"{text}\"");

            Assert.That(OpenPakBaas.TryHex(document.RootElement, out _), Is.False);
        }

        [Test]
        public void HexNeedsAString()
        {
            using JsonDocument document = JsonDocument.Parse("1234");

            Assert.That(OpenPakBaas.TryHex(document.RootElement, out _), Is.False);
        }

        // ---- friend list (contract A.1) ----

        private const string FriendItem = """
            {"friendId":"11cf3b1423e1183f",
             "friend":{"nickname":"Leia","thumbnailUrl":"https://example/1/a.jpg",
              "presence":{"state":"PLAYING","updatedAt":1789000000,
               "extras":{"friends":{"appInfo:appId":"0100a5a020d5e000","appInfo:acdIndex":1,
                "appInfo:presenceGroupId":"0100a5a020d5e000","appField":"{\"k\":\"v\"}"}}},
              "extras":{"self":{"playLog":"[{\"appInfo:appId\":\"0100a5a020d5e000\",\"appInfo:presenceGroupId\":\"0100a5a020d5e000\",\"totalPlayCount\":2,\"totalPlayTime\":60,\"firstPlayedAt\":1788000000,\"lastPlayedAt\":1788900000}]"}}},
             "isFavorite":true,"extras":{"self":{"isConfirmed":true,"isOnlineNotification":true,
              "route:appInfo:appId":"0100a5a020d5e000","route:name":"Leia"}},
             "createdAt":1788000001,"friendNote":"hi","channels":["FRIEND_CODE"]}
            """;

        [Test]
        public void ReadsAFriendInFull()
        {
            BaasFriend friend = OpenPakBaas.ParseFriend(Parse(FriendItem));

            Assert.That(friend, Is.Not.Null);
            Assert.That(friend.Id, Is.EqualTo(0x11cf3b1423e1183f));
            Assert.That(friend.Nickname, Is.EqualTo("Leia"));
            Assert.That(friend.State, Is.EqualTo(2));
            Assert.That(friend.UpdatedAt, Is.EqualTo(1789000000));
            Assert.That(friend.ApplicationId, Is.EqualTo(0x0100a5a020d5e000));
            Assert.That(friend.PresenceGroupId, Is.EqualTo(0x0100a5a020d5e000));
            Assert.That(friend.AcdIndex, Is.EqualTo(1));
            Assert.That(friend.AppField, Is.EqualTo("{\"k\":\"v\"}"));
            Assert.That(friend.IsFavorite, Is.True);
            Assert.That(friend.IsNewly, Is.False);
            Assert.That(friend.IsOnlineNotification, Is.True);
            Assert.That(friend.CreatedAt, Is.EqualTo(1788000001));
            Assert.That(friend.Note, Is.EqualTo("hi"));
            Assert.That(friend.Channel, Is.EqualTo(2));
            Assert.That(friend.Route.Name, Is.EqualTo("Leia"));
            Assert.That(friend.PlayLog, Has.Count.EqualTo(1));
        }

        [Test]
        public void OfflineFriendFallsBackToLogoutAt()
        {
            const string json = """
                {"friendId":"1","friend":{"nickname":"n","thumbnailUrl":"t",
                 "presence":{"state":"OFFLINE","updatedAt":10,"logoutAt":20}}}
                """;

            Assert.That(OpenPakBaas.ParseFriend(Parse(json)).UpdatedAt, Is.EqualTo(20));
        }

        [Test]
        public void AbsentConfirmFlagsTheFriendNewly()
        {
            const string json = """
                {"friendId":"1","friend":{"nickname":"n","thumbnailUrl":"t"},"extras":{}}
                """;

            Assert.That(OpenPakBaas.ParseFriend(Parse(json)).IsNewly, Is.True);
        }

        [Test]
        public void NonObjectAppFieldIsIgnored()
        {
            const string json = """
                {"friendId":"1","friend":{"nickname":"n","thumbnailUrl":"t",
                 "presence":{"state":"ONLINE","extras":{"friends":{"appField":"nope"}}}}}
                """;

            Assert.That(OpenPakBaas.ParseFriend(Parse(json)).AppField, Is.Null);
        }

        [Test]
        public void FriendNeedsItsThreeFields(
            [Values(
                """{"friend":{"nickname":"n","thumbnailUrl":"t"}}""",
                """{"friendId":"1","friend":{"thumbnailUrl":"t"}}""",
                """{"friendId":"1","friend":{"nickname":"n"}}""",
                """{"friendId":"1","friend":{"nickname":7,"thumbnailUrl":"t"}}""")]
            string json)
        {
            Assert.That(OpenPakBaas.ParseFriend(Parse(json)), Is.Null);
        }

        [Test]
        public void FriendListSkipsTheIncomplete()
        {
            const string json = """{"items":[{"friendId":"1"},{"friendId":"2","friend":{"nickname":"n","thumbnailUrl":"t"}}]}""";

            var friends = OpenPakBaas.ParseFriendList(Parse(json));

            Assert.That(friends, Has.Count.EqualTo(1));
            Assert.That(friends[0].Id, Is.EqualTo(2));
        }

        // ---- play log (contract A.1) ----

        [Test]
        public void ReadsTheDocumentedPlayLog()
        {
            const string text = """
                "[{\"appInfo:appId\":\"0100a5a020d5e000\",\"appInfo:acdIndex\":0,\"appInfo:presenceGroupId\":\"0100a5a020d5e000\",\"totalPlayCount\":12,\"totalPlayTime\":3600,\"firstPlayedAt\":1758000000,\"lastPlayedAt\":1758290000}]"
                """;

            var log = OpenPakBaas.ParsePlayLog(JsonDocument.Parse(text).RootElement.GetString());

            Assert.That(log, Has.Count.EqualTo(1));
            Assert.That(log[0].ApplicationId, Is.EqualTo(0x0100a5a020d5e000));
            Assert.That(log[0].TotalPlayCount, Is.EqualTo(12));
        }

        [Test]
        public void RepeatedAppIdsMergeWithTheLargerCountersWinning()
        {
            const string text = """
                "[{\"appInfo:appId\":\"1\",\"appInfo:presenceGroupId\":\"1\",\"totalPlayCount\":2,\"totalPlayTime\":9,\"firstPlayedAt\":4,\"lastPlayedAt\":5},{\"appInfo:appId\":\"1\",\"appInfo:presenceGroupId\":\"1\",\"totalPlayCount\":7,\"totalPlayTime\":3,\"firstPlayedAt\":8,\"lastPlayedAt\":6}]"
                """;

            var log = OpenPakBaas.ParsePlayLog(JsonDocument.Parse(text).RootElement.GetString());

            Assert.That(log, Has.Count.EqualTo(1));
            Assert.That(log[0].TotalPlayCount, Is.EqualTo(7));
            Assert.That(log[0].TotalPlayTime, Is.EqualTo(9));
            Assert.That(log[0].FirstPlayedAt, Is.EqualTo(8));
            Assert.That(log[0].LastPlayedAt, Is.EqualTo(6));
        }

        [Test]
        public void PlayLogNeedsBracketsAndCounters(
            [Values(null, "", "{}", "[]x", "not json", "[{\"appInfo:appId\":\"1\"}]")]
            string text)
        {
            Assert.That(OpenPakBaas.ParsePlayLog(text), Is.Empty);
        }

        // ---- requests (contract A.4) ----

        private const string InboxItem = """
            {"id":"a1","channels":["IN_APP"],"state":"pending","senderId":"11cf3b1423e1183f",
             "sender":{"nickname":"Leia","thumbnailUrl":"https://example/1/a.jpg"},
             "createdAt":1788000000,
             "extras":{"receiver":{"read":true},
              "senderAndReceiver":{"route:appInfo:appId":"0100a5a020d5e000","route:name":"Leia"}}}
            """;

        [Test]
        public void ReadsAnInboxRequest()
        {
            BaasRequest request = OpenPakBaas.ParseRequest(Parse(InboxItem), inbox: true);

            Assert.That(request, Is.Not.Null);
            Assert.That(request.Id, Is.EqualTo(0xa1));
            Assert.That(request.Channel, Is.EqualTo(3));
            Assert.That(request.State, Is.EqualTo(1));
            Assert.That(request.OtherId, Is.EqualTo(0x11cf3b1423e1183f));
            Assert.That(request.Nickname, Is.EqualTo("Leia"));
            Assert.That(request.CreatedAt, Is.EqualTo(1788000000));
            Assert.That(request.Read, Is.True);
            Assert.That(request.Route.ApplicationId, Is.EqualTo(0x0100a5a020d5e000));
            Assert.That(request.Route.Name, Is.EqualTo("Leia"));
        }

        [Test]
        public void OutboxReadsNamesFromTheSenderExtras()
        {
            const string json = """
                {"id":"a1","channels":["FRIEND_CODE"],"state":"AUTHORIZED","receiverId":"2",
                 "receiver":{"nickname":"n","thumbnailUrl":"t"},
                 "extras":{"sender":{"route:name":"Seen"},"senderAndReceiver":{"route:name":"Both"}}}
                """;

            BaasRequest request = OpenPakBaas.ParseRequest(Parse(json), inbox: false);

            Assert.That(request.State, Is.EqualTo(3));
            Assert.That(request.Route.Name, Is.EqualTo("Seen"));
            Assert.That(request.Read, Is.False);
        }

        [Test]
        public void RequestNeedsItsSixFields()
        {
            Assert.That(OpenPakBaas.ParseRequest(Parse("""{"id":"a1"}"""), inbox: true), Is.Null);
            Assert.That(OpenPakBaas.ParseRequest(Parse(InboxItem.Replace("\"sender\":", "\"receiver\":")),
                inbox: true), Is.Null);
        }

        [Test]
        public void OverlongNamesFailTheRequest()
        {
            string longName = new('n', 0x21);
            string json = InboxItem.Replace("\"Leia\"", $"\"{longName}\"");

            Assert.That(OpenPakBaas.ParseRequest(Parse(json), inbox: true), Is.Null);
        }

        [Test]
        public void RequestCountSplitsReadFromUnread()
        {
            const string json = """{"items":[{},{"extras":{"receiver":{"read":true}}},{"extras":{"receiver":{"read":false}}}]}""";

            Assert.That(OpenPakBaas.ParseRequestCount(Parse(json)), Is.EqualTo((2, 1)));
        }

        // ---- users and self (contract A.6, A.7) ----

        [Test]
        public void ReadsAFlatUser()
        {
            const string json = """{"id":"1","nickname":"n","thumbnailUrl":"t"}""";

            BaasUser user = OpenPakBaas.ParseUser(Parse(json));

            Assert.That(user, Is.Not.Null);
            Assert.That(user.Id, Is.EqualTo(1));
        }

        [Test]
        public void ReadsTheOwnUser()
        {
            const string json = """
                {"id":"1","nickname":"n","thumbnailUrl":"t",
                 "permissions":{"presence":"FRIENDS","friendRequestReception":true},
                 "links":{"friendCode":{"id":"SW-1234","regenerableAt":99}},
                 "extras":{"friends":{"playLog":"[]"}}}
                """;

            BaasUserSetting setting = OpenPakBaas.ParseUserSetting(Parse(json));

            Assert.That(setting.PresencePermission, Is.EqualTo(2));
            Assert.That(setting.PlayLogPermission, Is.EqualTo(3));
            Assert.That(setting.FriendRequestReception, Is.True);
            Assert.That(setting.FriendCode, Is.EqualTo("SW-1234"));
            Assert.That(setting.FriendCodeRegenerableAt, Is.EqualTo(99));
            Assert.That(setting.PlayLogText, Is.EqualTo("[]"));
        }

        [Test]
        public void EveryonePresenceBecomesSelf()
        {
            const string json = """{"id":"1","nickname":"n","thumbnailUrl":"t","permissions":{"presence":"EVERYONE"}}""";

            Assert.That(OpenPakBaas.ParseUserSetting(Parse(json)).PresencePermission, Is.EqualTo(0));
        }

        [Test]
        public void ReadsARelationship()
        {
            const string json = """{"isFriend":true,"isBlocking":false,"sentFriendRequestIds":["a1"]}""";

            Assert.That(OpenPakBaas.ParseRelationship(Parse(json)),
                Is.EqualTo(new BaasRelationship(true, false, true)));
        }

        // ---- blocks (contract A.8) ----

        [Test]
        public void ReadsABlock()
        {
            const string json = """
                {"items":[{"targetUserId":"5","targetUser":{"nickname":"n","thumbnailUrl":"t"},
                "createdAt":7,"extras":{"self":{"reason":"IN_APP"}}}]}
                """;

            var blocks = OpenPakBaas.ParseBlocks(Parse(json));

            Assert.That(blocks, Has.Count.EqualTo(1));
            Assert.That(blocks[0].Id, Is.EqualTo(5));
            Assert.That(blocks[0].Reason, Is.EqualTo(3));
            Assert.That(blocks[0].CreatedAt, Is.EqualTo(7));
        }

        [Test]
        public void OneBadBlockFailsTheWholeParse()
        {
            const string json = """{"items":[{"targetUserId":"5","targetUser":{"nickname":"n"}},{"targetUserId":"6"}]}""";

            Assert.That(OpenPakBaas.ParseBlocks(Parse(json)), Is.Null);
        }

        [Test]
        public void A101stBlockFailsTheWholeParse()
        {
            string items = string.Join(",", Enumerable.Repeat("""{"targetUserId":"1","targetUser":{"nickname":"n"}}""", 101));

            Assert.That(OpenPakBaas.ParseBlocks(Parse($"{{\"items\":[{items}]}}")), Is.Null);
        }

        // ---- invitations (invitations doc 2a, 2b) ----

        private const string InvitationItem = """
            {"id":1789287484056138,"invitation_group_id":1789287484056137,
             "sender_id":"11cf3b1423e1183f","application_id":"0100ed9024eb8000",
             "application_group_id":"0100ed9024eb8000","application_data":"RUNLQUFM",
             "read":false,"created_at":1789287484,"updated_at":1789287484}
            """;

        [Test]
        public void ReadsAnInvitation()
        {
            var invitations = OpenPakBaas.ParseInvitations(Parse($"{{\"items\":[{InvitationItem}]}}"));

            Assert.That(invitations, Has.Count.EqualTo(1));
            BaasInvitation invitation = invitations[0];
            Assert.That(invitation.Id, Is.EqualTo(1789287484056138));
            Assert.That(invitation.GroupId, Is.EqualTo(1789287484056137));
            Assert.That(invitation.SenderId, Is.EqualTo(0x11cf3b1423e1183f));
            Assert.That(invitation.ApplicationId, Is.EqualTo(0x0100ed9024eb8000));
            Assert.That(invitation.ApplicationData, Is.EqualTo(new byte[] { 0x45, 0x43, 0x4b, 0x41, 0x41, 0x4c }));
            Assert.That(invitation.Read, Is.False);
            Assert.That(invitation.CreatedAt, Is.EqualTo(1789287484));
        }

        [Test]
        public void EmptyInboxIsEmptyNotAFailure()
        {
            Assert.That(OpenPakBaas.ParseInvitations(Parse("""{"items":[]}""")), Is.Empty);
        }

        [Test]
        public void OneBadInvitationFailsTheWholeParse()
        {
            Assert.That(OpenPakBaas.ParseInvitations(Parse("""{"items":[{"id":1}]}""")), Is.Null);
        }

        [Test]
        public void ReadsAnInvitationGroup()
        {
            const string json = """
                {"id":9,"sender_id":"11cf3b1423e1183f","application_id":"0100ed9024eb8000",
                 "application_group_id":"0100ed9024eb8000","application_data":null,
                 "created_at":1789287484,"updated_at":1789287484,
                 "messages":{"en-US":"hi","ja":"\u3042"},
                 "invitations":[{"receiver_id":"f641399cf71a6314"}]}
                """;

            BaasInvitationGroup group = OpenPakBaas.ParseInvitationGroup(Parse(json));

            Assert.That(group, Is.Not.Null);
            Assert.That(group.Receivers, Has.Count.EqualTo(1));
            Assert.That(group.Messages[0], Is.EqualTo("hi"));
            Assert.That(group.Messages[2], Is.EqualTo("あ"));
            Assert.That(group.Messages[1], Is.Empty);
            Assert.That(group.ApplicationData, Is.Empty);
        }

        [Test]
        public void InvitationGroupNeedsReceivers(
            [Values(
                """{"id":9}""",
                """{"id":9,"sender_id":"1","application_id":"1","application_group_id":"1","application_data":null,"created_at":1,"updated_at":1,"messages":{},"invitations":[]}""")]
            string json)
        {
            Assert.That(OpenPakBaas.ParseInvitationGroup(Parse(json)), Is.Null);
        }

        // ---- request calls (contract A.4) ----

        [Test]
        public void RequestBoxUrl(
            [Values(true, false)] bool inbox)
        {
            string box = inbox ? "inbox" : "outbox";

            Assert.That(OpenPakBaas.RequestBoxUrl("h", "u", inbox, 8, 100), Is.EqualTo(
                $"https://h/2.0.0/users/u/friend_requests/{box}?offset=8&count=100&sort=createdAt:desc&filter.state.$eq=PENDING"));
        }

        [Test]
        public void RequestUrlNamesTheRequest()
        {
            Assert.That(OpenPakBaas.RequestUrl("h", 0xa1),
                Is.EqualTo("https://h/2.0.0/friend_requests/00000000000000a1"));
        }

        [Test]
        public void AnswerBodies(
            [Values("CANCELED", "AUTHORIZED", "REJECTED")] string state)
        {
            Assert.That(OpenPakBaas.AnswerRequestBody(state), Is.EqualTo(
                $"[{{\"op\":\"replace\",\"path\":\"/state\",\"value\":\"{state}\"}}]"));
        }

        [Test]
        public void AnswerBodyRejectsAnythingElse(
            [Values("CANCELLED", "cancelled", "", "APPROVED")] string state)
        {
            Assert.That(() => OpenPakBaas.AnswerRequestBody(state), Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void ReadBody()
        {
            Assert.That(OpenPakBaas.ReadRequestBody(), Is.EqualTo(
                """[{"op":"add","path":"/extras/receiver/read","value":true}]"""));
        }

        [Test]
        public void PlainSendBody()
        {
            Assert.That(OpenPakBaas.SendRequestBody("FRIEND_CODE", 1, 2), Is.EqualTo(
                """{"channels":["FRIEND_CODE"],"senderId":"0000000000000001","receiverId":"0000000000000002","extras":{"sender":{"route:sender":"me"}}}"""));
        }

        [Test]
        public void AppSendBodyKeepsTheModuleKeyOrder()
        {
            string body = OpenPakBaas.SendRequestBody("IN_APP", 1, 2,
                0x0100a5a020d5e000, 0, 0x0100a5a020d5e000, "Seen", "en-US", "Mine", "ja");

            Assert.That(body, Is.EqualTo(
                """{"channels":["IN_APP"],"senderId":"0000000000000001","receiverId":"0000000000000002","extras":{"sender":{"route:sender":"me","route:name":"Seen","route:name:language":"en-US"},"senderAndReceiver":{"route:appInfo:appId":"0100a5a020d5e000","route:appInfo:acdIndex":0,"route:appInfo:presenceGroupId":"0100a5a020d5e000","route:name":"Mine","route:name:language":"ja"}}}"""));
        }

        [Test]
        public void CatalogIdSplitsInHalves()
        {
            Assert.That(OpenPakBaas.TryCatalogId("0100a5a020d5e0000100a5a020d5e000", out ulong hi, out ulong lo), Is.True);
            Assert.That(hi, Is.EqualTo(0x0100a5a020d5e000));
            Assert.That(lo, Is.EqualTo(0x0100a5a020d5e000));
        }

        [Test]
        public void CatalogIdNeeds32Hex(
            [Values(null, "", "1", "0100a5a020d5e000", "0100a5a020d5e0000100a5a020d5e00000",
                "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] string catalogId)
        {
            Assert.That(OpenPakBaas.TryCatalogId(catalogId, out _, out _), Is.False);
        }

        // ---- channels ----

        [Test]
        public void ChannelTable(
            [Values("NX_FACED", "FRIEND_CODE", "IN_APP", "NINTENDO_ACCOUNT", "3DS",
                "NNID", "FACEBOOK", "TWITTER", "WECHAT", "CAMPUS")] string channel)
        {
            Assert.That(OpenPakBaas.ChannelOf(channel), Is.GreaterThan(0));
        }

        [Test]
        public void UnknownChannelIsZero()
        {
            Assert.That(OpenPakBaas.ChannelOf("BOGUS"), Is.EqualTo(0));
            Assert.That(OpenPakBaas.ChannelOf(null), Is.EqualTo(0));
        }
    }
}
