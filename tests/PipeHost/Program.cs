using System;
using System.Threading;
using InventorMCPBridge.Services;

namespace PipeHost
{
    // Levanta el BridgeService real —el mismo ensamblado que cargará Inventor— sobre un
    // pipe de pruebas, para poder atacarlo desde el cliente Python del servidor MCP.
    //
    // inventorApp es null a propósito: los handlers fallarán, que es justo lo que se mide
    // aquí (transporte, framing, UTF-8, propagación de errores), no la API de Inventor.
    //
    //   PipeHost.exe [nombre_pipe] [segundos]
    //     segundos <= 0  → hasta que lo maten (lo que hace run_all.ps1)
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string pipeName = args.Length > 0 ? args[0] : "InventorMCPBridgeTests";
            int seconds = args.Length > 1 ? int.Parse(args[1]) : 300;

            var bridge = new BridgeService(null, pipeName, work => work());
            bridge.Start();
            Console.WriteLine($"host escuchando en \\\\.\\pipe\\{pipeName}");
            Console.Out.Flush();

            if (seconds <= 0)
                Thread.Sleep(Timeout.Infinite);
            else
                Thread.Sleep(seconds * 1000);

            bridge.Stop();
            Console.WriteLine("host detenido");
            return 0;
        }
    }
}
