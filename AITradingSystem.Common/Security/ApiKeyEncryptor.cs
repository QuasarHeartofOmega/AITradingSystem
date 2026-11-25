using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AITradingSystem.Common.Security;

/// <summary>
/// Шифрование/дешифрование API-ключей (NFR-3.1).
/// Windows: DPAPI. Другие ОС: AES-256-GCM с мастер-ключом из TRADING_MASTER_KEY.
/// Формат строки: "enc:v1:dpapi:base64" или "enc:v1:aesgcm:base64".
/// </summary>
public static class ApiKeyEncryptor
{
    private const string DpapiPrefix = "enc:v1:dpapi:";
    private const string AesPrefix = "enc:v1:aesgcm:";

    private const int AesKeySize = 256;
    private const int AesIvSize = 12;
    private const int AesTagSize = 16;

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(plainText.Trim());

        if (OperatingSystem.IsWindows())
            return DpapiPrefix + Convert.ToBase64String(Dpapi.Protect(plainBytes));

        return AesPrefix + EncryptAesGcm(plainBytes);
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
            return string.Empty;

        cipherText = cipherText.Trim();

        bool isDpapi;
        string payload;
        if (cipherText.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            isDpapi = true;
            payload = cipherText[DpapiPrefix.Length..];
        }
        else if (cipherText.StartsWith(AesPrefix, StringComparison.Ordinal))
        {
            isDpapi = false;
            payload = cipherText[AesPrefix.Length..];
        }
        else
        {
            throw new CryptographicException(
                "Значение не зашифровано ApiKeyEncryptor (нет префикса enc:v1:). " +
                "Похоже, в appsettings.json вставлен открытый ключ — зашифруйте его утилитой SecretEncryptor.");
        }

        try
        {
            var cipherBytes = Convert.FromBase64String(payload);

            if (isDpapi)
            {
                if (OperatingSystem.IsWindows())
                    return Encoding.UTF8.GetString(Dpapi.Unprotect(cipherBytes));

                throw new PlatformNotSupportedException(
                    "Ключ зашифрован через DPAPI на Windows. На этой ОС он не расшифруется — перешифруйте на целевой машине.");
            }

            return DecryptAesGcm(cipherBytes);
        }
        catch (FormatException)
        {
            throw new CryptographicException(
                "Зашифрованный API-ключ повреждён: некорректный Base64. Скопируйте строку из утилиты целиком, без переносов.");
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException(
                "Не удалось расшифровать API-ключ. Возможные причины: другой пользователь Windows, другая машина, " +
                $"другой TRADING_MASTER_KEY. Детали: {ex.Message}");
        }
    }

    /// <summary>
    /// Весь Windows-специфичный код (DPAPI) изолирован здесь.
    /// Атрибут говорит анализатору: члены этого класса — только для Windows,
    /// вызывающий обязан проверить OperatingSystem.IsWindows().
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class Dpapi
    {
        // CurrentUser: расшифрует только тот же пользователь Windows.
        // Служба под другим аккаунтом → LocalMachine (и перешифровать ключи).
        private const DataProtectionScope Scope = DataProtectionScope.CurrentUser;

        public static byte[] Protect(byte[] data) =>
            ProtectedData.Protect(data, optionalEntropy: null, scope: Scope);

        public static byte[] Unprotect(byte[] data) =>
            ProtectedData.Unprotect(data, optionalEntropy: null, scope: Scope);
    }

    private static string EncryptAesGcm(byte[] plainBytes)
    {
        var key = GetAesKey();
        var iv = RandomNumberGenerator.GetBytes(AesIvSize);
        var tag = new byte[AesTagSize];
        var cipherBytes = new byte[plainBytes.Length];

        using var aes = new AesGcm(key, AesTagSize);
        aes.Encrypt(iv, plainBytes, cipherBytes, tag);

        // Формат: IV (12) + Tag (16) + CipherText
        var result = new byte[iv.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
        Buffer.BlockCopy(tag, 0, result, iv.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, iv.Length + tag.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static string DecryptAesGcm(byte[] data)
    {
        if (data.Length < AesIvSize + AesTagSize)
            throw new CryptographicException("Недопустимый формат зашифрованных данных.");

        var key = GetAesKey();
        var iv = new byte[AesIvSize];
        var tag = new byte[AesTagSize];
        var cipherBytes = new byte[data.Length - AesIvSize - AesTagSize];

        Buffer.BlockCopy(data, 0, iv, 0, AesIvSize);
        Buffer.BlockCopy(data, AesIvSize, tag, 0, AesTagSize);
        Buffer.BlockCopy(data, AesIvSize + AesTagSize, cipherBytes, 0, cipherBytes.Length);

        var plainBytes = new byte[cipherBytes.Length];
        using var aes = new AesGcm(key, AesTagSize);
        aes.Decrypt(iv, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// Мастер-ключ AES-256 из переменной окружения (32 байта в Base64).
    /// </summary>
    private static byte[] GetAesKey()
    {
        var b64 = Environment.GetEnvironmentVariable("TRADING_MASTER_KEY");
        if (string.IsNullOrWhiteSpace(b64))
            throw new CryptographicException(
                "Не задана переменная окружения TRADING_MASTER_KEY. " +
                "Сгенерируйте: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) и задайте её сервису.");

        var key = Convert.FromBase64String(b64);
        if (key.Length != AesKeySize / 8)
            throw new CryptographicException("TRADING_MASTER_KEY должен содержать ровно 32 байта (256 бит) в Base64.");

        return key;
    }
}