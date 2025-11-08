using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RoslynIndexer.Core.Services;


namespace RoslynIndexer.Tests.Services
{
    [TestClass]
    public class FileHasherTests
    {
        [TestMethod]
        [TestCategory("unit")]
        public void ComputeSha256_EmptyStream_ReturnsKnownHash()
        {
            // SHA-256("") = e3b0...b855
            using var ms = new MemoryStream();
            var hasher = new FileHasher();
            var hex = hasher.ComputeSha256(ms);
            Assert.AreEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hex);
        }


        [TestMethod]
        [TestCategory("unit")]
        public void ComputeSha256_SmallAscii_ReturnsKnownHash()
        {
            // SHA-256("abc") = ba78...15ad
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
            var hasher = new FileHasher();
            var hex = hasher.ComputeSha256(ms);
            Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hex);
        }


        [TestMethod]
        [TestCategory("unit")]
        public void ComputeSha256_LargeStream_StreamedWithoutExceptions()
        {
            // Build ~3.5MB stream to exercise the 1MB buffer in streaming loop
            var data = new byte[3_500_000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31);
            using var ms = new MemoryStream(data);


            var hasher = new FileHasher();
            var hex = hasher.ComputeSha256(ms);


            // Spot-check length & hex characters instead of pinning exact value
            Assert.AreEqual(64, hex.Length);
            foreach (char c in hex) Assert.IsTrue("0123456789abcdef".Contains(c));
        }
    }
}