namespace OwaDesktopCleaner;
using System.ComponentModel;
using System.Runtime.InteropServices;

internal static class WindowsCredentialStore
{
    const string Target = "APS.OwaDesktopCleaner:EWS:https://mail.apsfamily.com";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ReadNative(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint="CredWriteW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WriteNative(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint="CredDeleteW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteNative(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);

    public static (string User, string Password)? Read()
    {
        if(!ReadNative(Target,1,0,out var pointer)) {
            int error=Marshal.GetLastWin32Error();
            if(error==1168)return null;
            throw new Win32Exception(error);
        }
        Credential value=default;
        try {
            value=Marshal.PtrToStructure<Credential>(pointer);
            if(value.CredentialBlobSize>2560 || value.CredentialBlobSize%2!=0)throw new InvalidOperationException("Invalid credential format");
            return (value.UserName??"", Marshal.PtrToStringUni(value.CredentialBlob,(int)value.CredentialBlobSize/2)??"");
        } finally {
            if(value.CredentialBlob!=IntPtr.Zero && value.CredentialBlobSize<=2560)
                for(int i=0;i<value.CredentialBlobSize;i++)Marshal.WriteByte(value.CredentialBlob,i,0);
            CredFree(pointer);
        }
    }
    public static void Save(string user, string password)
    {
        if(password.Length>1280)throw new ArgumentException("Password exceeds credential storage limit");
        var blob=Marshal.StringToCoTaskMemUni(password);
        try {
            var value=new Credential { Type=1, TargetName=Target, UserName=user,
                CredentialBlob=blob, CredentialBlobSize=checked((uint)password.Length*2), Persist=2 };
            if(!WriteNative(ref value,0))throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
    public static void Delete()
    {
        if(!DeleteNative(Target,1,0)) {
            int error=Marshal.GetLastWin32Error();
            if(error!=1168)throw new Win32Exception(error);
        }
    }
}
