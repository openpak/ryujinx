using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.OpenPak;
using System;
using System.Text.Json;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// Reading the native invitation inbox. The fixture is one item exactly as a live OpenPak
    /// answered on 2026-09-13 for an invitation sent from a console — numeric ids included, which
    /// is the part a careless parser gets wrong.
    /// </summary>
    public class OpenPakInvitationTests
    {
        private const string InboxItem = """
            {"acd_index":0,"application_data":"AAEAAAD/////AQAAAAAAAAAGAQAAAAZVUVdGSEUL",
             "application_group_id":"0100ed9024eb8000","application_id":"0100ed9024eb8000",
             "application_id_match":false,"created_at":1789287484,
             "extras":{"receiver":{"read":false}},"id":1789287484056138,
             "invitation_group_id":1789287484056137,"invitation_type":"friend","read":false,
             "receiver_id":"f641399cf71a6314","sender_id":"11cf3b1423e1183f","updated_at":1789287484}
            """;

        [Test]
        public void ReadsOneInboxItem()
        {
            using JsonDocument item = JsonDocument.Parse(InboxItem);

            OpenPakInvitation invitation = OpenPakSession.InvitationOf(item.RootElement, "tobagin");

            // A u64 id read as a double loses its last digits, and the row would then act on an
            // invitation that does not exist.
            Assert.That(invitation.InvitationId, Is.EqualTo("1789287484056138"));
            Assert.That(invitation.TitleId, Is.EqualTo("0100ed9024eb8000"));
            Assert.That(invitation.From, Is.EqualTo("tobagin"));
            Assert.That(invitation.ExpiresAt,
                Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1789287484).UtcDateTime.AddHours(24)));
        }
    }
}
