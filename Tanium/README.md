# Tanium

Collection of Tanium config 

## How it works
- We run the script every 24H through a Tanium package to write a file on the OS.
- Tanium Sensor only read and parses the file every 24H, that was the sensor is extremely quick and not dependent on a script that runs on the machine.

## files
- `Network Neighbors LLDP [Linux].json` => Package to collect info from the Local file for Linux 
- `Network Neighbors LLDP [Windows].json` => Package to collect info from the Local file for Windows 
- `Network-Neighbors-LLDP-Sensor.json` => Package to collect info from the Local file for Windows 

## Features

- Uses pktmon.exe to filter and capture LLDP data.
- Parse and output the captured LLDP data.
- Default 30 second capture period, with -t <seconds> to increase capture time. Accepts 30-60 Seconds.
- Filters out ethernet devices which have wireless, wi-fi or bluetooth in their name.

## Requirements
- Needs to be run as Administrator in the console
- .Net 8
- pktmon.exe (Windows 10 and above)
- Writes to C:\temp\<lldp.etl and lldp.txt>, will clean up the files once done.

## Installation
- Copy the files where you want to and run clldp.exe, will also require .Net 8

## UI
    .\bin\Release\net8.0>clldp.exe
    Component ID: 868, Name: ASIX AX88772B USB2.0 to Fast Ethernet Adapter
    Enter the Component ID to capture on:
    868
    Capturing... 0 seconds remaining
    Parsed LLDP Data:
    Chassis ID: 68-FF-7B-B2-02-34
    Port ID: GigabitEthernet1/0/4
    Time to Live: TTL 120s
    System Name: T1500G-8T
    System Description: JetStream 8-Port Gigabit Smart Switch
    Management Address: 192.168.1.53
    Port Description: GigabitEthernet1/0/4 Interface
    VLANs:
    VLAN ID: 1 VLAN Name: System-VLAN

## Future Enhancements
- See if it's possible to capture CDP and parse it for CISCO switches.

## Alternatives
- https://github.com/chall32/LDWin
- https://www.powershellgallery.com/packages/PSDiscoveryProtocol
