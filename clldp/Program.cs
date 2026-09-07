using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Xml.Linq;

namespace LLDPParser
{
    // This class lets us group the informations from pktmon, mainly the CompID and the informations for the network driver of windows 
    // This lets us get nice infos such as if the NIC is up or not etc, its detailed config etc ...
    public class CombinedNicInfo
    {
        public string CompID { get; set; }
        public NetworkInterface nic { get; set; }
    }

    class Program
    {
        private static readonly string TempDirectory = Path.Combine("c:", "temp");
        private static readonly string EtlFilePath = Path.Combine(TempDirectory, "lldp.etl");
        private static readonly string TxtFilePath = Path.Combine(TempDirectory, "lldp.txt");
        private static readonly string TaniumOutputPath = Path.Combine(TempDirectory, "tanium-lldp.txt");


        static void Main(string[] args)
        {
            // Check for help argument
            if (args.Contains("/help") || args.Contains("/?"))
            {
                ShowHelp();
                return; // Exit after showing help
            }

            // Check for unsupported arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != "-debug" && args[i] != "-t" && args[i] != "-p" && args[i] != "/help" && args[i] != "/?" && args[i] != "-silent")
                {
                    // If the argument is not "-t" or "-p" and is not a valid argument, show error
                    if ((i == 0 || args[i - 1] != "-t") && (i == 0 || args[i - 1] != "-p"))
                    {
                        Console.WriteLine("Unsupported argument(s) detected.");
                        ShowHelp();
                        return;
                    }
                }
            }

            bool debugMode = args.Contains("-debug");
            // TODO 
            //debugMode = true;

            bool silentMode = args.Contains("-silent");
            // TODO
            //silentMode = true;

            // Parse optional -p argument
            string portArg = null;
            int pIndex = Array.IndexOf(args, "-p");
            if (pIndex != -1 && pIndex + 1 < args.Length)
            {
                portArg = args[pIndex + 1];
            }

            // Set default capture duration
            int captureDuration = 30;
            // Check if a custom duration is provided
            int tIndex = Array.IndexOf(args, "-t");
            if (tIndex != -1 && tIndex + 1 < args.Length && int.TryParse(args[tIndex + 1], out int parsedDuration))
            {
                captureDuration = ValidateCaptureDuration(parsedDuration);
            }

            try
            {

                // Check if running as administrator (required for pktmon)
                if (!IsRunningAsAdministrator())
                {
                    Console.WriteLine("Error: This application requires administrator privileges.");
                    Console.WriteLine("Please right-click and select 'Run as administrator'.");
                    return;
                }

                CleanUp(debugMode);
                // We remove the Tanium file to start fresh
                if (File.Exists(TaniumOutputPath)) File.Delete(TaniumOutputPath);
                EnsureDirectoryExists(TempDirectory);

                //// Uncomment and point to a file with the LLDP info to debug
                //var Data = ParseLldpData("C:\\temp\\VIZFRS901\\lldp.txt");
                //DisplayLldpData(Data);
                //DisplayLldpDataTanium(Data, null);


                // Get list of Nic that are UP and Ethernet
                List<CombinedNicInfo> compIDs = GetComponentIDs();
                List<CombinedNicInfo> selectedCompIDs = [];

                if (compIDs.Count > 0)
                {
                    selectedCompIDs = GetUserComponentSelection(compIDs, silentMode);

                    Console.WriteLine("Following NICs will be checked for LLDP");
                    foreach (CombinedNicInfo CombinedNicInfo in selectedCompIDs)
                        Console.WriteLine("- " + CombinedNicInfo.nic.Description);

                    // Loop over each Nic that has been selected
                    foreach (CombinedNicInfo selectedCompID in selectedCompIDs)
                    {
                        bool captureSuccessful = false;
                        bool firstTry = true;

                        while (!captureSuccessful)
                        {
                            Console.WriteLine($"selectedCompID: {selectedCompID.CompID}, NIC : {selectedCompID.nic.Description} ");

                            Socket s = null;

                            if (firstTry == false)
                            {
                                Console.WriteLine("No LLDP Data found, Let's try again with promiscious mode ...");
                                s = switchPromiscuousMode(selectedCompID);
                                CaptureLldpData(selectedCompID.CompID, captureDuration);
                            }
                            else
                            {
                                CaptureLldpData(selectedCompID.CompID, captureDuration);
                            }

                            // Closing the socket to shut off Promiscuous Mode
                            if (s != null)
                            {
                                Console.WriteLine("Closing Socket and ending Promiscuous Mode for NIC : "
                                    + selectedCompID.nic.Description
                                    + " | "
                                    + selectedCompID.nic.Name);
                                s.Close();
                            }

                            var lldpData = ParseLldpData(TxtFilePath);

                            if (lldpData.Count == 0)
                            {
                                Console.WriteLine("\nNo LLDP data captured. This may occur if no LLDP packets were transmitted during the capture window.");

                                // Silent mode runs without user input
                                if (silentMode == false)
                                {
                                    Console.Write("Would you like to retry the capture? (Y/N): ");

                                    string response = Console.ReadLine()?.Trim().ToUpper();

                                    if (response == "Y" || response == "YES")
                                    {
                                        Console.WriteLine("Retrying capture with the same configuration...\n");
                                        CleanUp(debugMode);
                                        continue;
                                    }
                                    else
                                    {
                                        Console.WriteLine("Capture cancelled.");
                                        break;
                                    }
                                }
                                // We check for the first try
                                // On the second try we switch to Promiscuous Mode for the NIC
                                else if (firstTry == true)
                                {
                                    firstTry = false;
                                    continue;
                                }
                                else
                                {
                                    Console.WriteLine("Capture cancelled.");
                                    // We still want to display the information that the script ran and write it to file
                                    DisplayLldpDataTanium(null, selectedCompID, silentMode);
                                    break;
                                }
                            }
                            else
                            {
                                captureSuccessful = true;
                                DisplayLldpData(lldpData);
                                DisplayLldpDataTanium(lldpData, selectedCompID, silentMode);

                                // If -p was provided, also write out results to a new file
                                if (!string.IsNullOrEmpty(portArg))
                                {
                                    string outPath = Path.Combine(
                                        TempDirectory,
                                        $"CLLDP_{DateTime.Now:yyyyMMddHHmmss}_{portArg}.txt"
                                    );
                                    WriteLldpDataToFile(lldpData, outPath);
                                }
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("No ethernet adapters found to capture on.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
            finally
            {
                CleanUp(debugMode);
            }
        }


        // Function that switches the selected NIC to Promiscuous Mode
        // Some NIC do not show LLDP packets without this setting enabled.
        // You can check that mode by using the powershell : Get-NetAdapter | Format-List -Property ifAlias,PromiscuousMode
        static Socket switchPromiscuousMode(CombinedNicInfo CombinedNicInfo)
        {
            try
            {
                Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, 1);

                //Big packet buffer in bytes
                // NOTE: You might need to play with this value if things don't work. Try factors of 1024 (for example, 1024, 8192, 24576, 1024000, etc)
                s.ReceiveBufferSize = 512000;

                //CombinedNicInfo.nic.GetIPProperties().GetIPv4Properties();
                //CombinedNicInfo.nic.GetIPProperties().UnicastAddresses;

                // uses Lambda to extract the Unicast IPv4 Address
                string ipv4Address = CombinedNicInfo.nic.GetIPProperties().UnicastAddresses.Where(n => n.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                                                                           .Select(g => g?.Address)
                                                                                           .Where(a => a != null)
                                                                                           .FirstOrDefault()
                                                                                           .ToString();

                Console.WriteLine("Switching to Promiscuous Mode for : " + CombinedNicInfo.nic.Description);

                s.Bind(new IPEndPoint(IPAddress.Parse(ipv4Address), 0));
                byte[] inBytes = new byte[] { 1, 0, 0, 0 };
                byte[] outBytes = new byte[] { 0, 0, 0, 0 };
                s.IOControl(IOControlCode.ReceiveAll, inBytes, outBytes);

                Console.WriteLine($"Promiscuous Mode has been enabled for {CombinedNicInfo.nic.Description} {ipv4Address}.");
                Console.WriteLine($"To turn off Promiscuous Mode for close this PowerShell window, or kill process PID: {Environment.ProcessId}");
                return s;
            }
            catch (SocketException se)
            {
                Console.WriteLine("Socket error: " + se.Message);
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unexpected error: " + ex.Message);
                return null;
            }
        }


        static int ValidateCaptureDuration(int duration)
        {
            if (duration < 30 || duration > 60)
            {
                Console.WriteLine($"Invalid capture duration: {duration} seconds. Duration must be between 30 and 60 seconds.");
                Console.WriteLine("Setting capture duration to default value of 30 seconds.");
                return 30;
            }
            return duration;
        }

        static bool IsRunningAsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(path);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: Failed to create directory {path}: {ex.Message}");
                    throw;
                }
            }
        }

        static void CaptureLldpData(string selectedCompID, int durationInSeconds)
        {
            ExecutePktmonCommand("filter add --ethertype 0x88cc");
            ExecutePktmonCommand($"start --capture --comp {selectedCompID} --pkt-size 0 -f {EtlFilePath}");

            DateTime endTime = DateTime.Now.AddSeconds(durationInSeconds);
            while (DateTime.Now < endTime)
            {
                int remainingSeconds = (int)(endTime - DateTime.Now).TotalSeconds;
                Console.Write($"\rCapturing... {remainingSeconds} seconds remaining ");
                Thread.Sleep(1000);
            }

            Console.WriteLine();
            ExecutePktmonCommand("stop");
            ExecutePktmonCommand($"format {EtlFilePath} -o {TxtFilePath} -v");
        }

        // Return list of NIC that 
        static List<CombinedNicInfo> GetComponentIDs()
        {
            var compIDs = new List<string>();
            string output = ExecutePktmonCommand("list");

            List<CombinedNicInfo> CombinedNicInfoList = [];

            if (!string.IsNullOrEmpty(output))
            {
                bool dataSectionStarted = false;

                // The format lets us format strings and align them for a nice output
                // Get all NICs
                Console.WriteLine("System NIC :");
                Console.WriteLine(string.Format("{0,-5} {1,-50} {2,-50}", "Status", "Name", "Description"));
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    CombinedNicInfo CombinedNicInfo = new()
                    {
                        nic = nic
                    };
                    CombinedNicInfoList.Add(CombinedNicInfo);

                    string formatInfos = string.Format("|{0,-5}| {1,-50}| {2,-50}|", nic.OperationalStatus, nic.Name, nic.Description);
                    Console.WriteLine(formatInfos);
                }

                string[] lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.None);

                foreach (var line in lines)
                {
                    if (!dataSectionStarted)
                    {
                        if (line.Contains("Address       Name"))
                        {
                            dataSectionStarted = true;
                        }
                        continue;
                    }

                    if (line.StartsWith("--") || line.Contains("--")) continue;

                    if (line.Trim().Length > 0)
                    {
                        string[] parts = line.Trim().Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            string compID = parts[0].Trim();
                            string MAC = parts[1].Trim();
                            string name = parts[2].Trim();

                            // Get list of all nic and map it to the Combined
                            foreach (CombinedNicInfo CombinedNicInfo in CombinedNicInfoList)
                            {
                                // Physical address of NIC is showned without "-" but pktmon is showned with "-"
                                if (CombinedNicInfo.nic.GetPhysicalAddress().ToString() == MAC.Replace("-", string.Empty))
                                    CombinedNicInfo.CompID = compID;
                            }
                        }
                    }
                }

                Console.WriteLine("Removing Nic not in PKTMON ...");
                // Uses a Lambda to find all CompID null (not found matching pktmon)
                var Removed = CombinedNicInfoList.RemoveAll(x => x.CompID == null);
                Console.WriteLine("Removed :" + Removed);

                Console.WriteLine("Removing Nic that aren't Ethernet ...");
                Removed = CombinedNicInfoList.RemoveAll(x => x.nic.Description.ToLower().Contains("bluetooth") ||
                                                             x.nic.Description.ToLower().Contains("wireless") ||
                                                             x.nic.Description.ToLower().Contains("mobile broadband") ||
                                                             x.nic.Description.ToLower().Contains("wi-fi"));
                Console.WriteLine("Removed :" + Removed);
            }

            return CombinedNicInfoList;
        }

        static List<CombinedNicInfo> GetUserComponentSelection(List<CombinedNicInfo> CombinedNicInfoList, bool silentMode)
        {
            if (CombinedNicInfoList.Count == 0)
                return null;

            List<CombinedNicInfo> AutoSelectedNicList = [];
            List<CombinedNicInfo> UserSelectedNicList = [];

            Console.WriteLine("\nAvailable Network Adapters:");
            // Display all adapters with their names

            Console.WriteLine(string.Format("|{0,-5}|{1,-6}|{2,-14}|{3,-60}|{4,-25}|", "ID", "Status", "MAC", "Description", "Name"));
            foreach (CombinedNicInfo CombinedNicInfo in CombinedNicInfoList)
            {
                string formatNics = string.Format("|{0,-5}|{1,-6}|{2,-14}|{3,-60}|{4,-25}|",
                    CombinedNicInfo.CompID,
                    CombinedNicInfo.nic.OperationalStatus.ToString(),
                    CombinedNicInfo.nic.GetPhysicalAddress(),
                    CombinedNicInfo.nic.Description,
                    CombinedNicInfo.nic.Name);

                Console.WriteLine(formatNics);

                if (CombinedNicInfo.nic.OperationalStatus == OperationalStatus.Up)
                    AutoSelectedNicList.Add(CombinedNicInfo);
            }

            while (true && silentMode == false)
            {
                Console.WriteLine("\nPress Enter to use the suggested adapter(s) above, or type a Component ID to use a different one:");
                string input = Console.ReadLine();

                // If user just pressed Enter, use the suggestion
                if (string.IsNullOrEmpty(input))
                {
                    foreach (var e in AutoSelectedNicList)
                        Console.WriteLine("Using adapter: " + e.nic.Name);
                    break;
                }
                else
                {
                    // Check if user entered a valid component ID
                    foreach (var e in AutoSelectedNicList)
                    {
                        if (input == e.CompID)
                        {
                            Console.WriteLine($"Using adapter: {input}");
                            UserSelectedNicList.Add(e);
                            return UserSelectedNicList;
                        }
                    }
                }
                Console.WriteLine("Invalid Component ID. Please try again or press Enter to use the suggested adapter:");
            }

            return AutoSelectedNicList;
        }

        static string ExecutePktmonCommand(string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo("pktmon", arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // Only show errors that are not benign status messages
                    if (!string.IsNullOrEmpty(error) &&
                        !error.Contains("not running") &&
                        !error.Contains("No filters") &&
                        arguments.Contains("start"))  // Only show errors during capture start
                    {
                        Console.WriteLine($"Warning: {error.Trim()}");
                    }

                    return output;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return null;
            }
        }

        static Dictionary<string, string> ParseLldpData(string filePath)
        {
            var lldpData = new Dictionary<string, string>();
            var vlanData = new List<string>();
            bool inLldpPacket = false;

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Warning: File not found: {filePath}");
                return lldpData;
            }

            using (var reader = new StreamReader(filePath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Detect LLDP packet sections
                    if (line.Contains("ethertype LLDP (0x88cc)"))
                    {
                        inLldpPacket = true;
                        continue;
                    }

                    // Skip non-LLDP packet data
                    if (!inLldpPacket)
                        continue;

                    if (line.Contains("End TLV (0)"))
                        inLldpPacket = false;  // End of LLDP packet

                    // Extract Chassis ID
                    if (line.Contains("Chassis ID TLV"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null && nextLine.Contains(": "))
                        {
                            string[] parts = nextLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Chassis ID"] = parts[1].Trim();
                        }
                    }
                    else if (line.Contains("Port ID TLV"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null && nextLine.Contains(": "))
                        {
                            string[] parts = nextLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Port ID"] = parts[1].Trim();
                        }
                    }
                    else if (line.Contains("Time to Live TLV"))
                    {
                        if (line.Contains("TTL"))
                        {
                            int ttlIndex = line.IndexOf("TTL");
                            if (ttlIndex >= 0)
                                lldpData["Time to Live"] = line.Substring(ttlIndex).Trim();
                        }
                    }
                    else if (line.Contains("Port Description TLV"))
                    {
                        if (line.Contains(": "))
                        {
                            string[] parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Port Description"] = parts[1].Trim();
                        }
                        else
                        {
                            var nextLine = reader.ReadLine();
                            if (nextLine != null)
                                lldpData["Port Description"] = nextLine.Trim();
                        }
                    }
                    else if (line.Contains("System Name TLV"))
                    {
                        if (line.Contains(": "))
                        {
                            string[] parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["System Name"] = parts[1].Trim();
                        }
                        else
                        {
                            var nextLine = reader.ReadLine();
                            if (nextLine != null)
                                lldpData["System Name"] = nextLine.Trim();
                        }
                    }
                    else if (line.Contains("System Description TLV"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null)
                            lldpData["System Description"] = nextLine.Trim();
                    }
                    else if (line.Contains("System Capabilities TLV"))
                    {
                        var capabilitiesLine = reader.ReadLine();
                        if (capabilitiesLine != null && capabilitiesLine.Contains(": "))
                        {
                            string[] parts = capabilitiesLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["System Capabilities"] = parts[1].Trim();
                        }

                        var enabledCapabilitiesLine = reader.ReadLine();
                        if (enabledCapabilitiesLine != null && enabledCapabilitiesLine.Contains(": "))
                        {
                            string[] parts = enabledCapabilitiesLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Enabled Capabilities"] = parts[1].Trim();
                        }
                    }
                    else if (line.Contains("Management Address TLV"))
                    {
                        var managementAddressLine = reader.ReadLine();
                        if (managementAddressLine != null && managementAddressLine.Contains("AFI IPv4 (1): "))
                        {
                            string[] parts = managementAddressLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Management Address"] = parts[1].Trim();
                        }
                    }
                    // Port VLAN ID
                    else if (line.Contains("Port VLAN Id Subtype"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null && nextLine.Contains("port vlan id (PVID):"))
                        {
                            string[] parts = nextLine.Split(new[] { ":" }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Port VLAN ID"] = parts[1].Trim();
                        }
                    }
                    // MAC/PHY Configuration
                    else if (line.Contains("MAC/PHY configuration/status Subtype"))
                    {
                        var autonegLine = reader.ReadLine();
                        var pmdLine = reader.ReadLine();
                        var mauLine = reader.ReadLine();

                        if (autonegLine != null && pmdLine != null)
                        {
                            string config = $"{autonegLine.Trim()}, {pmdLine.Trim()}";
                            if (mauLine != null)
                                config += $", {mauLine.Trim()}";

                            lldpData["MAC/PHY Configuration"] = config;
                        }
                    }
                    // Maximum Frame Size
                    else if (line.Contains("Max frame size Subtype"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null && nextLine.Contains("MTU size"))
                        {
                            string[] parts = nextLine.Split(new[] { "size" }, 2, StringSplitOptions.None);
                            if (parts.Length > 1)
                                lldpData["Maximum Frame Size"] = parts[1].Trim();
                        }
                    }
                    // Power via MDI
                    else if (line.Contains("Power via MDI Subtype"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null)
                            lldpData["Power via MDI"] = nextLine.Trim();
                    }
                    // Link Aggregation
                    else if (line.Contains("Link aggregation Subtype"))
                    {
                        var nextLine = reader.ReadLine();
                        if (nextLine != null)
                            lldpData["Link Aggregation"] = nextLine.Trim();
                    }
                    // Network Policy (Voice VLAN)
                    else if (line.Contains("Network policy Subtype"))
                    {
                        var appLine = reader.ReadLine();
                        var vlanLine = reader.ReadLine();

                        if (appLine != null && appLine.Contains("voice"))
                        {
                            string policy = appLine.Trim();
                            if (vlanLine != null)
                                policy += ", " + vlanLine.Trim();

                            lldpData["Voice Policy"] = policy;
                        }
                    }
                    // VLAN Names
                    else if (line.Contains("VLAN name Subtype"))
                    {
                        var vlanIdLine = reader.ReadLine();
                        var vlanNameLine = reader.ReadLine();

                        if (vlanIdLine != null && vlanNameLine != null)
                        {
                            string vlanId = vlanIdLine.Contains(":") ?
                                vlanIdLine.Split(new[] { ':' }, 2)[1].Trim() : "";

                            string vlanName = vlanNameLine.Contains(":") ?
                                vlanNameLine.Split(new[] { ':' }, 2)[1].Trim() : "";

                            if (!string.IsNullOrEmpty(vlanId) && !string.IsNullOrEmpty(vlanName))
                                vlanData.Add($"VLAN ID: {vlanId}, VLAN Name: {vlanName}");
                        }
                    }
                }
            }

            if (vlanData.Count > 0)
            {
                lldpData["VLANs"] = string.Join("\n", vlanData);
            }

            return lldpData;
        }

        static void DisplayLldpData(Dictionary<string, string> lldpData)
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine("         LLDP Capture Results");
            Console.WriteLine("========================================\n");

            // Define display order for better readability
            var displayOrder = new[]
            {
                "System Name",
                "Chassis ID",
                "Port ID",
                "Port Description",
                "Management Address",
                "System Description",
                "System Capabilities",
                "Enabled Capabilities",
                "Port VLAN ID",
                "Maximum Frame Size",
                "MAC/PHY Configuration",
                "Power via MDI",
                "Link Aggregation",
                "Voice Policy",
                "Time to Live",
                "VLANs"
            };

            foreach (var key in displayOrder)
            {
                if (lldpData.ContainsKey(key))
                {
                    if (key == "VLANs")
                    {
                        Console.WriteLine($"{key}:");
                        // Split and indent each VLAN line
                        string[] vlanLines = lldpData[key].Split('\n');
                        foreach (string vlanLine in vlanLines)
                        {
                            Console.WriteLine($"  {vlanLine}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"{key,-25}: {lldpData[key]}");
                    }
                }
            }

            Console.WriteLine("\n========================================\n");
        }

        static void DisplayLldpDataTanium(Dictionary<string, string> lldpData, CombinedNicInfo CombinedNicInfo, bool silentMode)
        {
            Console.WriteLine("\n========================================");
            Console.WriteLine("         LLDP Capture Results TANIUM FORMAT");
            Console.WriteLine("========================================\n");

            // We don't run this fuction if silentMode is not enabled
            if (silentMode == false)
                return;

            // Define display order for better readability
            var displayOrder = new[]
            {
                "System Name",
                "Chassis ID",
                "Port ID",
                "Port Description",
                "Management Address",
                "System Description",
                "System Capabilities",
                "Enabled Capabilities",
                "Port VLAN ID",
                "Maximum Frame Size",
                "MAC/PHY Configuration",
                "Power via MDI",
                "Link Aggregation",
                "Voice Policy",
                "Time to Live",
            };

            // Vue Tanium =>  Device|VLAN|Port|PortDescription|Adapter Name|Network Connection ID
            // Vue Windows => Device|VLAN|Port|PortDescription|Description |Name

            // ip8-dc-1136b|2469|Ethernet1/14|POC01_Port2|Ethernet 10Gb 2-port Adapter #2| FlexibleLOM 1 Port 2
            string TaniumOut = "";
            String date = DateTime.Now.ToString("yyyy-MM-dd");

            if (lldpData != null)
            {
                TaniumOut += date;
                TaniumOut += "|";
                TaniumOut += lldpData.TryGetValue("System Name", out var value) ? value : "NotFound";
                TaniumOut += "|";
                TaniumOut += lldpData.TryGetValue("Port VLAN ID", out var value1) ? value1 : "NotFound";
                TaniumOut += "|";
                TaniumOut += lldpData.TryGetValue("Port ID", out var value2) ? value2 : "NotFound";
                TaniumOut += "|";
                TaniumOut += lldpData.TryGetValue("Port Description", out var value3) ? value3 : "NotFound";
                TaniumOut += "|";
                TaniumOut += CombinedNicInfo.nic.Description;
                TaniumOut += "|";
                TaniumOut += CombinedNicInfo.nic.Name;
            }
            // If we don't get any data, that's fine, but we still want to know that the config ran
            else
            {
                TaniumOut += date;
                TaniumOut += "|";
                TaniumOut += "NotFound";
                TaniumOut += "|";
                TaniumOut += "NotFound";
                TaniumOut += "|";
                TaniumOut += "NotFound";
                TaniumOut += "|";
                TaniumOut += "NotFound";
                TaniumOut += "|";
                TaniumOut += CombinedNicInfo.nic.Description;
                TaniumOut += "|";
                TaniumOut += CombinedNicInfo.nic.Name;
            }


            Console.WriteLine(TaniumOut);
            Console.WriteLine("\n========================================\n");
            Console.WriteLine("Writing to file :" + TaniumOutputPath);

            using (var writer = new StreamWriter(TaniumOutputPath, true))
            {
                writer.WriteLine(TaniumOut);
            }

        }

        // New helper to write LLDP data to a file.
        static void WriteLldpDataToFile(Dictionary<string, string> lldpData, string path)
        {
            try
            {
                using (var writer = new StreamWriter(path, false))
                {
                    writer.WriteLine("========================================");
                    writer.WriteLine("         LLDP Capture Results");
                    writer.WriteLine($"         {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine("========================================");
                    writer.WriteLine();

                    // Define display order for better readability
                    var displayOrder = new[]
                    {
                        "System Name",
                        "Chassis ID",
                        "Port ID",
                        "Port Description",
                        "Management Address",
                        "System Description",
                        "System Capabilities",
                        "Enabled Capabilities",
                        "Port VLAN ID",
                        "Maximum Frame Size",
                        "MAC/PHY Configuration",
                        "Power via MDI",
                        "Link Aggregation",
                        "Voice Policy",
                        "Time to Live",
                        "VLANs"
                    };

                    foreach (var key in displayOrder)
                    {
                        if (lldpData.ContainsKey(key))
                        {
                            if (key == "VLANs")
                            {
                                writer.WriteLine($"{key}:");
                                // Split and indent each VLAN line
                                string[] vlanLines = lldpData[key].Split('\n');
                                foreach (string vlanLine in vlanLines)
                                {
                                    writer.WriteLine($"  {vlanLine}");
                                }
                            }
                            else
                            {
                                writer.WriteLine($"{key,-25}: {lldpData[key]}");
                            }
                        }
                    }

                    writer.WriteLine();
                    writer.WriteLine("========================================");
                }
                Console.WriteLine($"\nResults saved to: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to write results to file: {ex.Message}");
            }
        }

        static void CleanUp(bool debugMode)
        {
            ExecutePktmonCommand("stop");
            ExecutePktmonCommand("filter remove");
            ExecutePktmonCommand("reset");

            if (!debugMode)
            {
                if (File.Exists(EtlFilePath)) File.Delete(EtlFilePath);
                if (File.Exists(TxtFilePath)) File.Delete(TxtFilePath);
            }
        }

        static void ShowHelp()
        {
            Console.WriteLine("Usage: clldp.exe [options]");
            Console.WriteLine("Options:");
            Console.WriteLine("  -debug            Run the program in debug mode (keeps temp .etl and .txt files).");
            Console.WriteLine("  -t [duration]     Specify capture duration (must be between 30 and 60 seconds).");
            Console.WriteLine("  -p [port]         Write results to C:\\temp\\CLLDP_<timestamp>_<port>.txt in addition to console.");
            Console.WriteLine("  -silent           Run without user input");
            Console.WriteLine("  /help, /?         Display this help message.");
        }
    }
}