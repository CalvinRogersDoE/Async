using System.Runtime.InteropServices;
using System.Text;

namespace Async;

/// <summary>
/// The remote PC's password lives in Windows Credential Manager (a generic credential), never in
/// settings.json. The user types it into Windows' own credential dialog.
/// </summary>
static class Creds
{
    const int CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDUI_INFO
    {
        public int Size;
        public IntPtr Parent;
        public string MessageText, CaptionText;
        public IntPtr Banner;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL cred, int flags);
    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr cred);
    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    static extern int CredUIPromptForCredentials(ref CREDUI_INFO info, string target, IntPtr reserved, int error,
        StringBuilder user, int userMax, StringBuilder pass, int passMax, ref bool save, int flags);

    public static (string? User, string? Password) Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return (null, null);
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            string? pass = c.CredentialBlobSize > 0 ? Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2) : null;
            return (c.UserName, pass);
        }
        finally { CredFree(ptr); }
    }

    /// <summary>Shows Windows' credential dialog and stores what the user types. Returns false if cancelled.</summary>
    public static bool Prompt(string target, string user, string host)
    {
        var info = new CREDUI_INFO
        {
            Size = Marshal.SizeOf<CREDUI_INFO>(),
            CaptionText = "Async",
            MessageText = $"Sign-in for {host}: its Windows user and password, or for a Mac the name and password you log into it with.",
        };
        var u = new StringBuilder(user, 514);
        var p = new StringBuilder(256);
        bool save = false;
        // GENERIC_CREDENTIALS | ALWAYS_SHOW_UI | DO_NOT_PERSIST | EXCLUDE_CERTIFICATES
        int rc = CredUIPromptForCredentials(ref info, target, IntPtr.Zero, 0, u, 514, p, 256, ref save, 0x40000 | 0x80 | 0x2 | 0x8);
        if (rc != 0) return false;

        string pass = p.ToString();
        p.Clear();
        var blob = Marshal.StringToHGlobalUni(pass);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = u.ToString(),
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(pass.Length * 2),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                Comment = $"Async sign-in for {host}",
            };
            if (!CredWrite(ref cred, 0)) { Log.Write($"CredWrite failed: {Marshal.GetLastWin32Error()}"); return false; }
            return true;
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(blob); }
    }
}
