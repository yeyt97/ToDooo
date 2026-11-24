using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ToDooo.Services;

/// <summary>
/// 用 Windows DPAPI（当前用户范围）加解密日历账号的应用专用密码。
/// 密文只能在同一 Windows 用户下解开，把 settings.json 拷走也拿不到明文。
/// </summary>
public static class SecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        var input = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(Transform(input, protect: true));
    }

    public static string Unprotect(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return string.Empty;
        }

        try
        {
            var input = Convert.FromBase64String(cipherText);
            return Encoding.UTF8.GetString(Transform(input, protect: false));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
        catch (CryptographicException)
        {
            // 换了 Windows 账号或密文损坏时解不出来，当作未配置处理。
            return string.Empty;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        DataBlob output = default;
        try
        {
            var blob = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref blob, "ToDooo.Calendar", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }

            inputHandle.Free();
        }
    }
}
