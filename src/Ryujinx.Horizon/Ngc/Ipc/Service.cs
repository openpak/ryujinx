using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Ngc;
using Ryujinx.Horizon.Sdk.Ngc.Detail;
using Ryujinx.Horizon.Sdk.Sf;
using Ryujinx.Horizon.Sdk.Sf.Hipc;
using System;

namespace Ryujinx.Horizon.Ngc.Ipc
{
    partial class Service : INgcService
    {
        private readonly ProfanityFilter _profanityFilter;

        public Service(ProfanityFilter profanityFilter)
        {
            _profanityFilter = profanityFilter;
        }

        [CmifCommand(0)]
        public Result GetContentVersion(out uint version)
        {
            lock (_profanityFilter)
            {
                return _profanityFilter.GetContentVersion(out version);
            }
        }

        [CmifCommand(1)]
        public Result Check(
            out uint checkMask,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> text,
            uint regionMask,
            ProfanityFilterOption option)
        {
            lock (_profanityFilter)
            {
                Result result = _profanityFilter.CheckProfanityWords(out checkMask, text, regionMask, option);
                // OpenPak diagnostic: every Check the game makes, with its verdict.
                try
                {
                    int n = text.IndexOf((byte)0); if (n < 0) n = text.Length; n = Math.Min(n, 128);
                    Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.ServiceNgc,
                        $"[OpenPak] Check in={System.Text.Encoding.UTF8.GetString(text[..n])} region=0x{regionMask:x} -> result=0x{result.ErrorCode:x} mask=0x{checkMask:x}");
                }
                catch { }
                return result;
            }
        }

        [CmifCommand(2)]
        public Result Mask(
            out int maskedWordsCount,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> filteredText,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> text,
            uint regionMask,
            ProfanityFilterOption option)
        {
            lock (_profanityFilter)
            {
                int length = Math.Min(filteredText.Length, text.Length);

                text[..length].CopyTo(filteredText[..length]);

                return _profanityFilter.MaskProfanityWordsInText(out maskedWordsCount, filteredText, regionMask, option);
            }
        }

        [CmifCommand(3)]
        public Result Reload()
        {
            lock (_profanityFilter)
            {
                return _profanityFilter.Reload();
            }
        }
    }
}
