namespace Serpy.Core.Qemu;

/// <summary>
/// Immutable QEMU argument list for a specific launch profile.
/// All QEMU argument construction goes through this type — no ad-hoc string building elsewhere.
/// </summary>
public sealed class QemuArguments
{
    private readonly List<string> _args = [];

    public IReadOnlyList<string> Args => _args;

    // ── Machine / accelerator ─────────────────────────────────────────────

    public QemuArguments Machine(string type = "q35") => Add("-machine", type);

    public QemuArguments Accelerator(AcceleratorResult accel) =>
        Add("-accel", AcceleratorPolicy.AccelArg(accel.Accelerator));

    public QemuArguments Cpu(string model = "host") => Add("-cpu", model);

    public QemuArguments Smp(int cores) => Add("-smp", cores.ToString());

    public QemuArguments Memory(int mb) => Add("-m", $"{mb}M");

    // ── Disks ─────────────────────────────────────────────────────────────

    public QemuArguments SystemDisk(string imagePath, string format = "qcow2") =>
        Add("-drive", $"file={imagePath},format={format},if=virtio,index=0");

    /// <summary>
    /// Persistent data disk using the Windows-valid fsync-honoring policy (KTD6):
    /// cache=none,aio=threads for WHPX. Policy comes from AcceleratorResult.
    /// </summary>
    public QemuArguments DataDisk(string imagePath, AcceleratorResult accel) =>
        Add("-drive",
            $"file={imagePath},format=raw,if=virtio,index=1," +
            $"cache={accel.DataDiskCache},aio={accel.DataDiskAio}");

    public QemuArguments Cdrom(string isoPath) =>
        Add("-drive", $"file={isoPath},media=cdrom,readonly=on");

    // ── Network ──────────────────────────────────────────────────────────

    /// <summary>
    /// QEMU user-mode NAT: forward host:<paramref name="hostPort"/> → guest :80 (nginx).
    /// No bridge, no LAN exposure.
    /// </summary>
    public QemuArguments UserNetWithPortForward(int hostPort) =>
        Add("-netdev", $"user,id=net0,hostfwd=tcp:127.0.0.1:{hostPort}-:80")
        .Add("-device", "virtio-net-pci,netdev=net0");

    // ── Display ───────────────────────────────────────────────────────────

    public QemuArguments Headless() => Add("-nographic");

    // ── TLS credentials object (server, mutual auth) ──────────────────────

    /// <summary>
    /// QEMU -object tls-creds-x509 for one chardev endpoint.
    /// <paramref name="certDir"/> must contain ca-cert.pem, server-cert.pem, server-key.pem.
    /// </summary>
    public QemuArguments TlsCredsX509(string id, string certDir) =>
        Add("-object",
            $"tls-creds-x509,id={id},endpoint=server,verify-peer=yes,dir={certDir}");

    // ── Chardev TCP socket (TLS-authenticated) ────────────────────────────

    public QemuArguments TlsChardev(string chardevId, int port, string tlsCredsId) =>
        Add("-chardev",
            $"socket,id={chardevId},host=127.0.0.1,port={port}," +
            $"server=on,wait=off,tls-creds={tlsCredsId}");

    /// <summary>Attach a chardev to the guest's first UART (/dev/ttyS0).</summary>
    public QemuArguments SerialOnChardev(string chardevId) =>
        Add("-serial", $"chardev:{chardevId}");

    // ── QMP monitor ───────────────────────────────────────────────────────

    public QemuArguments QmpOnChardev(string chardevId) =>
        Add("-mon", $"chardev={chardevId},mode=control,pretty=off");

    // ── QGA virtio-serial ─────────────────────────────────────────────────

    public QemuArguments VirtioSerialDevice() =>
        Add("-device", "virtio-serial");

    public QemuArguments QgaVirtioPort(string chardevId) =>
        Add("-device", $"virtserialport,chardev={chardevId},name=org.qemu.guest_agent.0");

    // ── Boot / misc ────────────────────────────────────────────────────────

    public QemuArguments VmName(string name) => Add("-name", name);

    public QemuArguments FirmwareDir(string libDir) => Add("-L", libDir);

    /// <summary>Start paused (for smoke tests); do not use in production start.</summary>
    public QemuArguments Paused() => Add("-S");

    // ── Private helper ────────────────────────────────────────────────────

    private QemuArguments Add(params string[] parts)
    {
        _args.AddRange(parts);
        return this;
    }
}
