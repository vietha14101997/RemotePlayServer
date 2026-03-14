#nullable enable
using System;
using System.Diagnostics;

namespace RemotePlayServer.Infrastructure.Network;

/// <summary>
/// Checks and creates Windows Firewall rules for the server.
/// Ensures both TCP (WebSocket) and UDP (LAN discovery) ports are open.
/// </summary>
public static class FirewallHelper
{
    private const string RuleName = "RemotePlayServer";
    private const string DiscoveryRuleName = "RemotePlayServer-Discovery";

    /// <summary>
    /// Ensure firewall rules exist for the server executable (TCP) and UDP discovery port.
    /// Creates rules automatically if missing. Requires admin privileges for creation.
    /// </summary>
    public static void EnsureRules(int tcpPort, int udpDiscoveryPort)
    {
        EnsureAppRule();
        EnsureUdpRule(udpDiscoveryPort);
    }

    /// <summary>
    /// Ensure an inbound allow rule exists for the server executable (all ports/protocols).
    /// </summary>
    private static void EnsureAppRule()
    {
        if (RuleExists(RuleName))
        {
            Console.WriteLine($"[Firewall] Rule '{RuleName}' exists ✓");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Firewall] Rule '{RuleName}' not found — creating...");
        Console.ResetColor();

        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Firewall] Cannot determine exe path, skipping rule creation");
            Console.ResetColor();
            return;
        }

        RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any");
    }

    /// <summary>
    /// Ensure an inbound UDP rule exists for LAN discovery broadcast.
    /// </summary>
    private static void EnsureUdpRule(int port)
    {
        if (RuleExists(DiscoveryRuleName))
        {
            Console.WriteLine($"[Firewall] Rule '{DiscoveryRuleName}' exists ✓");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Firewall] Rule '{DiscoveryRuleName}' not found — creating...");
        Console.ResetColor();

        RunNetsh($"advfirewall firewall add rule name=\"{DiscoveryRuleName}\" dir=in action=allow protocol=UDP localport={port} enable=yes profile=any");
    }

    /// <summary>
    /// Check if a named firewall rule already exists.
    /// </summary>
    private static bool RuleExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{name}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            // netsh returns "No rules match" or error when rule doesn't exist
            return proc.ExitCode == 0 && !output.Contains("No rules match", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Run a netsh command. Logs success/failure.
    /// </summary>
    private static void RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Firewall] Failed to start netsh");
                Console.ResetColor();
                return;
            }

            string output = proc.StandardOutput.ReadToEnd();
            string error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            if (proc.ExitCode == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[Firewall] Rule created successfully ✓");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Firewall] Failed (exit {proc.ExitCode}). Run server as Administrator to create firewall rules.");
                if (!string.IsNullOrEmpty(error)) Console.WriteLine($"  {error.Trim()}");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Firewall] Error: {ex.Message}. Run server as Administrator.");
            Console.ResetColor();
        }
    }
}
