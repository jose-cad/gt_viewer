namespace GtViewer.Web.Models;

public static class MeshRights
{
    // MeshCentral Rights Constants (from apprelays.js / meshipkvm.js)
    public const long MESHRIGHT_EDITMESH = 0x00000001; // 1
    public const long MESHRIGHT_MANAGEUSERS = 0x00000002; // 2
    public const long MESHRIGHT_MANAGECOMPUTERS = 0x00000004; // 4
    public const long MESHRIGHT_REMOTECONTROL = 0x00000008; // 8 - Ver e controlar tela
    public const long MESHRIGHT_AGENTCONSOLE = 0x00000010; // 16 - Terminal remoto
    public const long MESHRIGHT_SERVERFILES = 0x00000020; // 32 - Acesso a arquivos
    public const long MESHRIGHT_WAKEDEVICE = 0x00000040; // 64
    public const long MESHRIGHT_SETNOTES = 0x00000080; // 128
    public const long MESHRIGHT_REMOTEVIEWONLY = 0x00000100; // 256
    public const long MESHRIGHT_NOTERMINAL = 0x00000200; // 512
    public const long MESHRIGHT_NOFILES = 0x00000400; // 1024
    public const long MESHRIGHT_NOAMT = 0x00000800; // 2048
    public const long MESHRIGHT_DESKLIMITEDINPUT = 0x00001000; // 4096
    public const long MESHRIGHT_LIMITEVENTS = 0x00002000; // 8192
    public const long MESHRIGHT_CHATNOTIFY = 0x00004000; // 16384
    public const long MESHRIGHT_UNINSTALL = 0x00008000; // 32768
    public const long MESHRIGHT_NODESKTOP = 0x00010000; // 65536
    public const long MESHRIGHT_REMOTECOMMAND = 0x00020000; // 131072 - Executar comandos
    public const long MESHRIGHT_RESETOFF = 0x00040000; // 262144
    public const long MESHRIGHT_GUESTSHARING = 0x00080000; // 524288
    public const long MESHRIGHT_DEVICEDETAILS = 0x00100000; // 1048576
    public const long MESHRIGHT_RELAY = 0x00200000; // 2097152
    public const long MESHRIGHT_NOREGISTRY = 0x00400000; // 4194304
    public const long MESHRIGHT_ADMIN = 0xFFFFFFFF;

    /// <summary>
    /// Bitmask estritamente mínimo para o usuário espelho em suporte remoto:
    /// APENAS MESHRIGHT_REMOTECONTROL (8).
    /// Sem terminal (16), sem arquivos (32), sem comandos (131072) e sem administração.
    /// </summary>
    public const long MinimalRemoteDesktopRights = MESHRIGHT_REMOTECONTROL;

    public static bool HasRemoteControl(long rights) => (rights & MESHRIGHT_REMOTECONTROL) != 0;
    public static bool HasTerminal(long rights) => (rights & MESHRIGHT_AGENTCONSOLE) != 0;
    public static bool HasFiles(long rights) => (rights & MESHRIGHT_SERVERFILES) != 0;
    public static bool HasCommands(long rights) => (rights & MESHRIGHT_REMOTECOMMAND) != 0;
    public static bool HasManageUsers(long rights) => (rights & MESHRIGHT_MANAGEUSERS) != 0;
    public static bool HasManageComputers(long rights) => (rights & MESHRIGHT_MANAGECOMPUTERS) != 0;
}
