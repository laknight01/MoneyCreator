using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

public static class SecurityHelper
{
    // PC 고유의 SID를 가져와서 암호화 키로 사용 (소스 코드에 키를 숨길 필요 없음)
    private static string GetMachineKey()
    {
        return WindowsIdentity.GetCurrent().User.Value;
    }

    public static void SaveEncryptedPassword(string password, string filePath)
    {
        byte[] key = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(GetMachineKey()));
        byte[] iv = new byte[16]; // 단순화를 위해 0으로 채운 IV 사용 (실제론 랜덤 IV가 더 좋음)

        using (Aes aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;

            using (FileStream fs = new FileStream(filePath, FileMode.Create))
            using (CryptoStream cs = new CryptoStream(fs, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                byte[] data = Encoding.UTF8.GetBytes(password);
                cs.Write(data, 0, data.Length);
            }
        }
    }

    public static string LoadDecryptedPassword(string filePath)
    {
        if (!File.Exists(filePath)) return string.Empty;

        byte[] key = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(GetMachineKey()));
        byte[] iv = new byte[16];

        using (Aes aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;

            using (FileStream fs = new FileStream(filePath, FileMode.Open))
            using (CryptoStream cs = new CryptoStream(fs, aes.CreateDecryptor(), CryptoStreamMode.Read))
            using (StreamReader sr = new StreamReader(cs))
            {
                return sr.ReadToEnd();
            }
        }
    }
}