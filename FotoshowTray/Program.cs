namespace FotoshowTray;

/// <summary>
/// Entry point.
///
/// Modos de arranque:
///   (sin args)              → primera instancia → inicia tray service
///   --add-file "path"       → forwarded desde context menu del Explorer
///   --add-folder "path"     → forwarded desde context menu del Explorer
///   --auth-token "jwt"      → callback del login Google (URI scheme fotoshow://auth)
///
/// Si ya hay una instancia corriendo, los comandos se envían via named pipe y el proceso secundario sale.
/// </summary>
static class Program
{
    private static readonly Mutex _singleInstance =
        new(true, "Global\\FotoshowTray_SingleInstance");

    [STAThread]
    static async Task Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ─── comandos forwarded desde context menu ─────────────────────────────
        if (args.Length >= 2)
        {
            switch (args[0])
            {
                case "--add-file":
                    await ForwardOrProcess("add_file", args[1]);
                    return;

                case "--add-folder":
                    await ForwardOrProcess("add_folder", args[1]);
                    return;

                case "--auth-token":
                    await ForwardOrSaveToken(args[1]);
                    return;
            }
        }

        // ─── single instance check ─────────────────────────────────────────────
        if (!_singleInstance.WaitOne(0, false))
        {
            // Ya hay una instancia corriendo — solo enviar ping y salir
            await PipeClient.SendAsync("ping");
            return;
        }

        try
        {
            Application.Run(new TrayApp());
        }
        finally
        {
            _singleInstance.ReleaseMutex();
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────────

    private static async Task ForwardOrProcess(string action, string path)
    {
        // Si el tray ya está corriendo, enviar via pipe
        if (PipeClient.IsTrayRunning())
        {
            await PipeClient.SendAsync(action, path);
            return;
        }

        // Si no está corriendo, iniciar el tray en background y procesar el comando
        // Esto pasa cuando el usuario hace clic derecho antes de que el tray arranque
        if (_singleInstance.WaitOne(0, false))
        {
            var tray = new TrayApp();
            // Esperar a que el pipe server levante
            await Task.Delay(1500);
            await PipeClient.SendAsync(action, path);
            Application.Run(tray);
            _singleInstance.ReleaseMutex();
        }
        else
        {
            // Otro proceso ganó la carrera — esperar y enviar
            await Task.Delay(2000);
            await PipeClient.SendAsync(action, path);
        }
    }

    private static async Task ForwardOrSaveToken(string token)
    {
        // Guardar token en config
        var config = TrayConfig.Load();
        config.JwtToken = token;
        config.Save();

        // Notificar al tray si está corriendo
        if (PipeClient.IsTrayRunning())
            await PipeClient.SendAsync("auth_token", token);
    }
}
