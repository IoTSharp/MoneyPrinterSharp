using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoProduction;

/// <summary>只通过当前 Windows 用户的凭据管理器保存和读取接口密钥。</summary>
public static class CredentialStore
{
    public const string DefaultTarget = "Codex:VideoDemo:MoarkApiKey";
    private const uint Generic = 1;
    private const int NotFound = 1168;

    /// <summary>读取通用凭据；错误只包含错误号，不回显目标内容或密钥。</summary>
    public static string Read(string target)
    {
        ValidateTarget(target);
        if (!CredRead(target, Generic, 0, out var pointer))
            throw new InvalidOperationException($"无法读取 Windows 凭据（错误 {Marshal.GetLastWin32Error()}）。");
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.BlobSize == 0 || credential.BlobSize > 2560 || credential.BlobSize % 2 != 0)
                throw new InvalidDataException("Windows 凭据内容无效。");
            var bytes = new byte[credential.BlobSize];
            try
            {
                Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                var secret = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(secret) || secret.Contains('\r') || secret.Contains('\n'))
                    throw new InvalidDataException("Windows 凭据内容无效。");
                return secret;
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    /// <summary>以 UTF-16 通用凭据写入当前用户保险库，兼容已有 Python 读取方式。</summary>
    public static void Write(string target, string secret)
    {
        ValidateTarget(target);
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 1280 || secret.Contains('\r') || secret.Contains('\n'))
            throw new ArgumentException("凭据必须非空、无换行，且不超过1280字符。");
        var bytes = Encoding.Unicode.GetBytes(secret);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic, TargetName = target, BlobSize = (uint)bytes.Length,
                Blob = pointer, Persist = 2, UserName = "Moark API"
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException($"无法保存 Windows 凭据（错误 {Marshal.GetLastWin32Error()}）。");
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>只查询凭据是否存在，不读取或输出凭据值。</summary>
    public static bool Exists(string target)
    {
        ValidateTarget(target);
        if (CredRead(target, Generic, 0, out var pointer)) { CredFree(pointer); return true; }
        var error = Marshal.GetLastWin32Error();
        if (error == NotFound) return false;
        throw new Win32Exception(error, "查询 Windows 凭据失败。");
    }

    /// <summary>拒绝非 Windows 平台及无法安全作为凭据名称的输入。</summary>
    private static void ValidateTarget(string target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("凭据管理器仅支持 Windows。");
        if (string.IsNullOrWhiteSpace(target) || target.Length > 256 || target.Any(char.IsControl))
            throw new ArgumentException("凭据目标名称无效。");
    }

    /// <summary>对应 Windows CREDENTIALW 的字段布局。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    /// <summary>调用系统读取通用凭据。</summary>
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    /// <summary>调用系统写入通用凭据。</summary>
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    /// <summary>归还 Windows 分配的凭据内存。</summary>
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
