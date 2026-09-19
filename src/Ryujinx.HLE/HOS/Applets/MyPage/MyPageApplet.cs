using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.HLE.HOS.Services.Am.AppletAE;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.OpenPak;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ryujinx.HLE.HOS.Applets.MyPage
{
    /// <summary>
    /// The friends applet (MyPage, AppletId 0x1A), as far as a game can drive it.
    ///
    /// The SDK pushes CommonArguments, then one 0x10A8 argument: u32 type at +0, the caller's Uid at
    /// +8, a type-specific payload from +0x18. For types 2, 8 and 9 it pops a u32 from the out
    /// channel and returns it as the call's Result; the other types read nothing back. Only the
    /// invitation types do anything here. The rest are the console's friend screens, which live in
    /// the OpenPak window instead, and complete at once.
    ///
    /// Every path ends with the applet finished: a game waiting on a library applet that never
    /// returns is a hung game, and whatever went wrong is at most a non-zero Result to it.
    /// </summary>
    internal class MyPageApplet : IApplet
    {
        private const int DataMax = 0x400;
        private const int DescriptionSlot = 0xC0;

        // Friends module (121). The Result myPage gives for a cancelled picker was never recovered;
        // this stands in for it, and for a send that failed. It only has to be non-zero and not
        // 2121-0030, which the SDK reserves for an applet that did not run.
        private const uint ResultCancelled = 121 | (1 << 9);

        // The GameModeDescription slots, in the order the friends module and the Unity enum agree on.
        private static readonly string[] _languages =
        [
            "en-US", "en-GB", "ja", "fr", "de", "es-419", "es", "it",
            "nl", "fr-CA", "pt", "ru", "zh-Hans", "zh-Hant", "ko", "pt-BR",
        ];

        // A send is a sign-in (at worst) and one POST; longer than this and the game is told no.
        private static readonly TimeSpan _sendTimeout = TimeSpan.FromSeconds(20);

        private readonly Horizon _system;

        public event EventHandler AppletStateChanged;

        public MyPageApplet(Horizon system)
        {
            _system = system;
        }

        public ResultCode Start(AppletSession normalSession, AppletSession interactiveSession)
        {
            uint type = uint.MaxValue;
            uint? result = null;

            try
            {
                normalSession.Pop(); // CommonArguments

                byte[] argument = normalSession.Pop();

                type = BinaryPrimitives.ReadUInt32LittleEndian(argument);

                result = type switch
                {
                    8 => StartFriendInvitation(argument),
                    9 => StartSendingFriendInvitation(argument),
                    2 => Skip(type, 0),
                    _ => Skip(type, null),
                };
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAm, $"MyPage type {type}: {exception.Message}");

                result = type is 2 or 8 or 9 ? ResultCancelled : null;
            }

            if (result.HasValue)
            {
                byte[] output = new byte[4];

                BinaryPrimitives.WriteUInt32LittleEndian(output, result.Value);

                normalSession.Push(output);
            }

            AppletStateChanged?.Invoke(this, null);

            _system.ReturnFocus();

            return ResultCode.Success;
        }

        private static uint? Skip(uint type, uint? result)
        {
            Logger.Info?.Print(LogClass.ServiceAm, $"MyPage type {type} requested; the friend screens are in the OpenPak window");

            return result;
        }

        /// <summary>Type 8: the person picks up to maxInviteeCount friends, then it is sent.</summary>
        private uint StartFriendInvitation(byte[] argument)
        {
            int max = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(argument.AsSpan(0x18)), 1, 16);
            byte[] data = Data(argument, 0x20, 0x28);
            List<(string, string)> messages = Messages(argument, 0x428);

            Logger.Info?.Print(LogClass.ServiceAm, $"MyPage StartFriendInvitation: up to {max}, {data.Length} bytes of data");

            IReadOnlyList<OpenPakFriend> picked = _system.Device.UIHandler.ShowFriendInvitationDialog(max);

            if (picked == null || picked.Count == 0)
            {
                Logger.Info?.Print(LogClass.ServiceAm, "MyPage StartFriendInvitation: cancelled");

                return ResultCancelled;
            }

            using CancellationTokenSource giveUp = new(_sendTimeout);

            List<string> receivers = [];

            foreach (OpenPakFriend friend in picked.Take(max))
            {
                string id = OpenPakSession.Instance.ReceiverIdAsync(friend, giveUp.Token).GetAwaiter().GetResult();

                if (id == null)
                {
                    Logger.Warning?.Print(LogClass.ServiceAm, $"MyPage: no BAAS user for {friend.DisplayName}; left out");

                    continue;
                }

                receivers.Add(id);
            }

            return Send(receivers, data, messages, giveUp.Token);
        }

        /// <summary>Type 9: the game chose the invitees itself, so it is sent as it stands.</summary>
        private uint StartSendingFriendInvitation(byte[] argument)
        {
            int count = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(argument.AsSpan(0x18)), 0, 16);
            byte[] data = Data(argument, 0xA0, 0xA8);
            List<(string, string)> messages = Messages(argument, 0x4A8);

            Logger.Info?.Print(LogClass.ServiceAm, $"MyPage StartSendingFriendInvitation: {count} invitee(s), {data.Length} bytes of data");

            using CancellationTokenSource giveUp = new(_sendTimeout);

            List<string> receivers = [];

            for (int index = 0; index < count; index++)
            {
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(argument.AsSpan(0x20 + index * 8));

                receivers.Add(OpenPakSession.Instance.ReceiverIdAsync(id, giveUp.Token).GetAwaiter().GetResult());
            }

            return Send(receivers, data, messages, giveUp.Token);
        }

        private uint Send(List<string> receivers, byte[] data, List<(string, string)> messages, CancellationToken cancellationToken)
        {
            if (receivers.Count == 0)
            {
                return ResultCancelled;
            }

            ProcessResult application = _system.Device.Processes.ActiveApplication;

            ulong applicationId = application?.ProgramId ?? 0;
            ulong groupId = application?.ApplicationControlProperties.PresenceGroupId ?? 0;

            bool sent = OpenPakSession.Instance.SendInvitationAsync(
                receivers, applicationId, groupId != 0 ? groupId : applicationId, data, messages, cancellationToken)
                    .GetAwaiter().GetResult();

            return sent ? 0 : ResultCancelled;
        }

        private static byte[] Data(byte[] argument, int sizeOffset, int dataOffset)
        {
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(argument.AsSpan(sizeOffset)), DataMax);

            return argument.AsSpan(dataOffset, size).ToArray();
        }

        /// <summary>The GameModeDescription's filled slots: UTF-8, NUL-terminated, empty ones skipped.</summary>
        private static List<(string, string)> Messages(byte[] argument, int offset)
        {
            List<(string, string)> messages = [];

            for (int slot = 0; slot < _languages.Length; slot++)
            {
                ReadOnlySpan<byte> text = argument.AsSpan(offset + slot * DescriptionSlot, DescriptionSlot);

                int end = text.IndexOf((byte)0);

                if (end < 0)
                {
                    end = text.Length;
                }

                if (end > 0)
                {
                    messages.Add((_languages[slot], Encoding.UTF8.GetString(text[..end])));
                }
            }

            return messages;
        }
    }
}
