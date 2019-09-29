using System.IO;
using System.Security.Cryptography;
using Wabbajack.Common;

namespace Wabbajack
{
    public class HashCache
    {
        public static HashCache Instance { get; } = new HashCache();


        private HashCache() { }


        private string ComputeDigest(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            Utils.Status($"Hashing {fileName}");

            var sha = new SHA256Managed();
            using (var o = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            {
                using (var i = File.OpenRead(filePath))
                {
                    i.CopyToWithStatus(new FileInfo(filePath).Length, o, $"Hashing {fileName}");
                }
            }

            return sha.Hash.ToBase64();
        }

        public string GetFileHash(string filePath)
        {
            var cachePath = filePath + ".sha";
            if (cachePath.FileExists() && new FileInfo(cachePath).LastWriteTime >= new FileInfo(filePath).LastWriteTime)
                return File.ReadAllText(cachePath);

            File.WriteAllText(cachePath, ComputeDigest(filePath));
            return GetFileHash(filePath);
        }
    }
}
