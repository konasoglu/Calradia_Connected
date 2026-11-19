using System;
using Common.Users;

namespace CalradiaConnected.Network.Auth
{
    public class AccountManager
    {
        private static AccountManager? _instance;
        public static AccountManager Instance => _instance ??= new AccountManager();

        public bool AuthenticateOrRegister(string username, string password, out int accountId, out bool isNewAccount, out string reason)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 2)
            {
                accountId = 0;
                isNewAccount = false;
                reason = "Kullanıcı adı en az 2 karakter olmalıdır.";
                return false;
            }

            string cleanUser = username.Trim();
            var existingUser = UserDatabase.Instance.GetUser(cleanUser);
            isNewAccount = existingUser == null;

            bool success = UserDatabase.Instance.ValidateLogin(cleanUser, password, out string errorReason);
            if (success)
            {
                var user = UserDatabase.Instance.GetUser(cleanUser);
                accountId = user?.AccountId ?? 1001;
                reason = isNewAccount ? "Hesabınız başarıyla oluşturuldu ve giriş yapıldı." : "Giriş başarılı!";
                return true;
            }
            else
            {
                accountId = 0;
                reason = string.IsNullOrEmpty(errorReason) ? "Giriş reddedildi." : errorReason;
                return false;
            }
        }
    }
}
