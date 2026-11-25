using System.Text;
using AITradingSystem.Common.Security;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"Пользователь: {Environment.UserName}, машина: {Environment.MachineName}");
Console.WriteLine("Запускать под тем же аккаунтом и на той же машине, где работает бот!\n");

Console.Write("API Key:    "); var apiKey = ReadSecret();
Console.Write("Secret Key: "); var apiSecret = ReadSecret();

var encKey = ApiKeyEncryptor.Encrypt(apiKey);
var encSecret = ApiKeyEncryptor.Encrypt(apiSecret);

// Самопроверка тем же классом, которым бот будет расшифровывать
if (ApiKeyEncryptor.Decrypt(encKey) != apiKey.Trim() ||
    ApiKeyEncryptor.Decrypt(encSecret) != apiSecret.Trim())
{
    Console.WriteLine("ОШИБКА: самопроверка не прошла, ничего не копируйте!");
    return;
}

Console.WriteLine("\nOK. В appsettings.json:\n");
Console.WriteLine($"\"ApiKeyEncrypted\": \"{encKey}\",");
Console.WriteLine($"\"ApiSecretEncrypted\": \"{encSecret}\"");

static string ReadSecret()
{
    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Remove(sb.Length - 1, 1); continue; }
        if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}