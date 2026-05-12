namespace System.Net;

public static class IPAddressExtensions
{
    extension(IPAddress address)
    {
        public IPAddress? Next()
        {
            ArgumentNullException.ThrowIfNull(address);

            byte[] bytes = address.GetAddressBytes();

            for (int i = bytes.Length - 1; i >= 0; i--)
            {
                if (bytes[i] < 255)
                {
                    bytes[i]++;
                    return new IPAddress(bytes);
                }
                bytes[i] = 0;
            }
            return null; // Overflow, no next address
        }

        public bool TryNext(out IPAddress next)
        {
            next = address.Next()!;

            return next is null;
        }
    }
}
