using System.IO;
using System.IO.Compression;

namespace Assimalign.IO.Mkv.Tests
{
    public class TestFiles
    {



        public void Download()
        {

            var zipPath         = "C:\\Users\\c.crawford\\Downloads\\Test.zip";
            var zipExtractPath  = "C:\\Users\\c.crawford\\Downloads\\Test";
            var downloadUri     = "https://sourceforge.net/projects/matroska/files/test_files/matroska_test_w1_1.zip/download";

            using var client = new HttpClient()
            {
                Timeout = TimeSpan.FromMinutes(5),
            };
            using var request = new HttpRequestMessage()
            {
                Method      = HttpMethod.Get,
                RequestUri  = new Uri(downloadUri)
            };

            using var response = client.Send(request);

            if (response.IsSuccessStatusCode)
            {
                using var responseStream = response.Content.ReadAsStream();
                using var fileStream = new FileStream(zipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

                while (responseStream.Position < responseStream.Length)
                {
                    var size = 4096;
                    var buffer = new byte[size];
                    var remaining = (responseStream.Length - responseStream.Position);
                    var length = remaining > size ? size : (int)remaining;

                    responseStream.Read(buffer, 0, length);

                    fileStream.Write(buffer, 0, length);
                }

                fileStream.Flush();
                fileStream.Close();

                ZipFile.ExtractToDirectory(zipPath, zipExtractPath);
            }
        }



        [Fact]
        public void Test1()
        {
            Download();
        }
    }
}