using Gmc320s;

namespace Gmc320s.Console;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var port = args.Length > 0 ? args[0] : null;

        using var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            System.Console.WriteLine("Stopping...");
            cts.Cancel();
        };

        try
        {
            Gmc320sClient gmc;
            string version;

            if (port is not null)
            {
                System.Console.WriteLine($"Connecting to GMC-320S on {port}...");
                gmc = Gmc320sClient.Connect(port);
                version = await gmc.GetVersionAsync(cts.Token);
            }
            else
            {
                System.Console.WriteLine("No port specified; scanning COM1-COM5 for a GMC device...");
                try
                {
                    (gmc, version) = await Gmc320sClient.FindPortAsync(
                        onStatus: msg => System.Console.WriteLine($"  {msg}"), cancellationToken: cts.Token);
                }
                catch (IOException)
                {
                    System.Console.Error.WriteLine("Error: no GMC device found on COM1-COM5.");
                    return 5;
                }

                port = gmc.PortName;
                System.Console.WriteLine($"Found GMC device on {port}.");
            }

            using (gmc)
            {
                System.Console.WriteLine($"Version: {version}");
                System.Console.WriteLine($"CPM: {await gmc.GetCpmAsync(cts.Token)}");
                System.Console.WriteLine($"Voltage: {await gmc.GetVoltageAsync(cts.Token):F1} V");
                System.Console.WriteLine($"Temperature: {await gmc.GetTemperatureCelsiusAsync(cts.Token):F1} °C");
                System.Console.WriteLine($"Device time: {await gmc.GetDateTimeAsync(cts.Token):yyyy-MM-dd HH:mm:ss}");
                System.Console.WriteLine("Live CPS (Ctrl+C to stop):");

                await foreach (var cps in gmc.ReadCpsAsync(null, cts.Token))
                    System.Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss}  {cps,6} CPS");
            }

            System.Console.WriteLine("Stopped.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            System.Console.WriteLine("Stopped.");
            return 0;
        }
        catch (TimeoutException ex)
        {
            System.Console.Error.WriteLine($"Error: the device did not respond in time ({ex.Message})");
            System.Console.Error.WriteLine("Check that the GMC-320S is powered on, the USB cable is connected, the port/baud rate are correct, and that no other program (e.g. GQ's own software) has the port open.");
            return 2;
        }
        catch (UnauthorizedAccessException)
        {
            System.Console.Error.WriteLine($"Error: could not open {port} - it may already be in use by another program.");
            return 3;
        }
        catch (IOException)
        {
            var available = System.IO.Ports.SerialPort.GetPortNames();
            System.Console.Error.WriteLine($"Error: {port} could not be opened.");
            System.Console.Error.WriteLine(available.Length > 0
                ? $"Available ports: {string.Join(", ", available)}"
                : "No serial ports were detected on this machine.");
            return 4;
        }
        catch (ArgumentException ex)
        {
            var available = System.IO.Ports.SerialPort.GetPortNames();
            System.Console.Error.WriteLine($"Error: invalid port '{port}' ({ex.Message})");
            System.Console.Error.WriteLine(available.Length > 0
                ? $"Available ports: {string.Join(", ", available)}"
                : "No serial ports were detected on this machine.");
            return 4;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Unexpected error: {ex}");
            return 1;
        }
    }
}
