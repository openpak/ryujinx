using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Shim;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.OpenPak;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.Acc
{
    public class AccountManager : IEmulatorAccountManager
    {
        public static readonly UserId DefaultUserId = new("00000000000000010000000000000000");

        private readonly AccountSaveDataManager _accountSaveDataManager;

        // Todo: The account service doesn't have the permissions to delete save data. Qlaunch takes care of deleting
        // save data, so we're currently passing a client with full permissions. Consider moving save data deletion
        // outside of the AccountManager.
        private readonly HorizonClient _horizonClient;

        private readonly ConcurrentDictionary<string, UserProfile> _profiles;
        private UserProfile[] _storedOpenedUsers;

        public UserProfile LastOpenedUser { get; private set; }

        public AccountManager(HorizonClient horizonClient, string initialProfileName = null)
        {
            _horizonClient = horizonClient;

            _profiles = new ConcurrentDictionary<string, UserProfile>();
            _storedOpenedUsers = [];

            _accountSaveDataManager = new AccountSaveDataManager(_profiles);

            // A linked profile goes by its OpenPak name and picture, as a console user does by
            // its account's.
            OpenPakSession.Instance.SignedInAs += (profileId, nickname) =>
            {
                string name = nickname.Trim();
                name = name[..Math.Min(name.Length, 0x20)];

                if (_profiles.TryGetValue(profileId, out UserProfile profile) && profile.Name != name)
                {
                    SetUserName(profile.UserId, name);
                }

                _ = AdoptAvatarAsync(profileId);
            };

            if (!_profiles.TryGetValue(DefaultUserId.ToString(), out _))
            {
                byte[] defaultUserImage = EmbeddedResources.Read("Ryujinx.HLE/HOS/Services/Account/Acc/DefaultUserImage.jpg");

                AddUser("RyuPlayer", defaultUserImage, DefaultUserId);

                OpenUser(DefaultUserId);
            }
            else
            {
                UserId commandLineUserProfileOverride = default;
                if (!string.IsNullOrEmpty(initialProfileName))
                {
                    commandLineUserProfileOverride = _profiles.Values.FirstOrDefault(x => x.Name == initialProfileName)?.UserId ?? default;
                    if (commandLineUserProfileOverride.IsNull)
                    {
                        Logger.Warning?.Print(LogClass.Application, $"The command line specified profile named '{initialProfileName}' was not found");
                    }
                }

                OpenUser(commandLineUserProfileOverride.IsNull ? _accountSaveDataManager.LastOpened : commandLineUserProfileOverride);
            }
        }

        public void AddUser(string name, byte[] image, UserId userId = new())
        {
            if (userId.IsNull)
            {
                userId = new UserId(Guid.NewGuid().ToString().Replace("-", string.Empty));
            }

            UserProfile profile = new(userId, name, image);

            _profiles.AddOrUpdate(userId.ToString(), profile, (key, old) => profile);

            _accountSaveDataManager.Save(_profiles);
        }

        public void OpenUser(UserId userId)
        {
            if (_profiles.TryGetValue(userId.ToString(), out UserProfile profile))
            {
                // TODO: Support multiple open users ?
                foreach (UserProfile userProfile in GetAllUsers())
                {
                    if (userProfile == LastOpenedUser)
                    {
                        userProfile.AccountState = AccountState.Closed;

                        break;
                    }
                }

                (LastOpenedUser = profile).AccountState = AccountState.Open;

                _accountSaveDataManager.LastOpened = userId;

                // The OpenPak identity follows the open profile: each profile is its own account.
                OpenPakConfig.SetProfile(userId.ToString(), profile.Name);
            }

            _accountSaveDataManager.Save(_profiles);
        }

        public void CloseUser(UserId userId)
        {
            if (_profiles.TryGetValue(userId.ToString(), out UserProfile profile))
            {
                profile.AccountState = AccountState.Closed;
            }

            _accountSaveDataManager.Save(_profiles);
        }

        public void OpenUserOnlinePlay(Uid userId)
        {
            OpenUserOnlinePlay(new UserId((long)userId.Low, (long)userId.High));
        }

        public void OpenUserOnlinePlay(UserId userId)
        {
            if (_profiles.TryGetValue(userId.ToString(), out UserProfile profile))
            {
                // TODO: Support multiple open online users ?
                foreach (UserProfile userProfile in GetAllUsers())
                {
                    if (userProfile == LastOpenedUser)
                    {
                        userProfile.OnlinePlayState = AccountState.Closed;

                        break;
                    }
                }

                profile.OnlinePlayState = AccountState.Open;
            }

            _accountSaveDataManager.Save(_profiles);
        }

        public void CloseUserOnlinePlay(Uid userId)
        {
            CloseUserOnlinePlay(new UserId((long)userId.Low, (long)userId.High));
        }

        public void CloseUserOnlinePlay(UserId userId)
        {
            if (_profiles.TryGetValue(userId.ToString(), out UserProfile profile))
            {
                profile.OnlinePlayState = AccountState.Closed;
            }

            _accountSaveDataManager.Save(_profiles);
        }

        /// <summary>
        /// The picture follows the account on every sign-in, the way the name does. OpenPak serves
        /// one for every account — the person's own, or the network's default — and a picture
        /// changed on the website arrives under a new url, so the profile keeps up with it.
        /// Written only when it differs from what the profile already holds.
        /// </summary>
        private async Task AdoptAvatarAsync(string profileId)
        {
            try
            {
                byte[] avatar = await OpenPakSession.Instance.AvatarAsync(CancellationToken.None);

                if (avatar is not { Length: > 0 } || !_profiles.TryGetValue(profileId, out UserProfile profile))
                {
                    return;
                }

                byte[] image = ProfileImage(avatar);

                if (image.Length > 0 && !image.AsSpan().SequenceEqual(profile.Image))
                {
                    SetUserImage(profile.UserId, image);
                }
            }
            catch (Exception exception)
            {
                // The profile keeps the picture it has; being signed in is what mattered.
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Could not take the account's picture: {exception.Message}");
            }
        }

        /// <summary>
        /// A picture as a profile stores one: the 256x256 JPEG a console's user image is, whatever
        /// the source was. Empty when the bytes are not an image anything here can read.
        /// </summary>
        public static byte[] ProfileImage(byte[] buffer)
        {
            try
            {
                using SKBitmap bitmap = SKBitmap.Decode(buffer);
                using SKBitmap resized = bitmap?.Resize(new SKImageInfo(256, 256), new SKSamplingOptions(SKFilterMode.Linear));

                if (resized == null)
                {
                    return [];
                }

                using SKImage image = SKImage.FromBitmap(resized);
                using SKData jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 100);

                return jpeg.ToArray();
            }
            catch (Exception)
            {
                // Skia throws instead of answering null when the bytes are not an image at all.
                return [];
            }
        }

        public void SetUserImage(UserId userId, byte[] image)
        {
            foreach (UserProfile userProfile in GetAllUsers())
            {
                if (userProfile.UserId == userId)
                {
                    userProfile.Image = image;

                    break;
                }
            }

            _accountSaveDataManager.Save(_profiles);
        }

        public void SetUserName(UserId userId, string name)
        {
            foreach (UserProfile userProfile in GetAllUsers())
            {
                if (userProfile.UserId == userId)
                {
                    userProfile.Name = name;

                    break;
                }
            }

            if (userId == LastOpenedUser?.UserId)
            {
                OpenPakConfig.SetProfile(userId.ToString(), name);
            }

            _accountSaveDataManager.Save(_profiles);
        }

        public void DeleteUser(UserId userId)
        {
            DeleteSaveData(userId);

            _profiles.Remove(userId.ToString(), out _);

            _ = OpenPakSession.ForgetProfileAsync(userId.ToString());

            OpenUser(DefaultUserId);

            _accountSaveDataManager.Save(_profiles);
        }

        private void DeleteSaveData(UserId userId)
        {
            SaveDataFilter saveDataFilter = SaveDataFilter.Make(programId: default, saveType: default,
                new LibHac.Fs.UserId((ulong)userId.High, (ulong)userId.Low), saveDataId: default, index: default);

            using UniqueRef<SaveDataIterator> saveDataIterator = new();

            _horizonClient.Fs.OpenSaveDataIterator(ref saveDataIterator.Ref, SaveDataSpaceId.User, in saveDataFilter).ThrowIfFailure();

            Span<SaveDataInfo> saveDataInfo = stackalloc SaveDataInfo[10];

            while (true)
            {
                saveDataIterator.Get.ReadSaveDataInfo(out long readCount, saveDataInfo).ThrowIfFailure();

                if (readCount == 0)
                {
                    break;
                }

                for (int i = 0; i < readCount; i++)
                {
                    _horizonClient.Fs.DeleteSaveData(SaveDataSpaceId.User, saveDataInfo[i].SaveDataId).ThrowIfFailure();
                }
            }
        }

        internal int GetUserCount()
        {
            return _profiles.Count;
        }

        internal bool TryGetUser(UserId userId, out UserProfile profile)
        {
            return _profiles.TryGetValue(userId.ToString(), out profile);
        }

        public IEnumerable<UserProfile> GetAllUsers()
        {
            return _profiles.Values;
        }

        internal IEnumerable<UserProfile> GetOpenedUsers()
        {
            return _profiles.Values.Where(x => x.AccountState == AccountState.Open);
        }

        internal IEnumerable<UserProfile> GetStoredOpenedUsers()
        {
            return _storedOpenedUsers;
        }

        internal void StoreOpenedUsers()
        {
            _storedOpenedUsers = _profiles.Values.Where(x => x.AccountState == AccountState.Open).ToArray();
        }

        internal UserProfile GetFirst()
        {
            return _profiles.First().Value;
        }
    }
}
