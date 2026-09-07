## Author : Thomas HERBIN / Dec 2025
## Usage : This script automatically get LLDPCLI informations and output them to a file in a format for Tanium
## Format is as Follow :
## Date|Device|VLAN|Port|PortDescription|Adapter Name|Network Connection ID
## examples :
## 2026-01-01|ip8-dc-1136a|2469|Ethernet1/14|POC01_Port2|Ethernet 10Gb 2-port Adapter #1| FlexibleLOM 1 Port 1
## 2026-01-01|ip8-dc-1136a|2469|Ethernet1/14|Linux_test|Ethernet Controller 10G X550T (X550-TX 10 Gig LOM)| eno1

import datetime
import subprocess
import json
import pathlib

## Mandatory for Tanium to Work
import sys
import tanium
import traceback


tanium_lldp = "/root/.tanium-lldp.txt"

## Cleanup the file
pathlib.Path(tanium_lldp).unlink(missing_ok=True)

f = open(tanium_lldp, 'w')

try:
  lldpProcess = subprocess.run(['lldpcli', 'show', 'neighbors', 'details', '-f' 'json'], capture_output=True, text=True)
except OSError as e:
  print("lldpcli not installed", file=f)
  exit(1)

if lldpProcess.returncode != 0:
  print("lldpcli error", file=f)
  exit(1)


today_date = str(datetime.date.today())

lldpresults = lldpProcess.stdout

data = json.loads(lldpresults)

json_formatted_str = json.dumps(data, indent=2)
#print(json_formatted_str)

if len(data['lldp']) == 0:
  print("No neighbors Found", file=f)
  exit (0)

try:
  for interface in data['lldp']['interface']:
    # if only one interface, we go one level above
    if type(interface) is str:
      interface=data['lldp']['interface']

    # Get Key of each interface
    ifname = list(interface.keys())[0]
    chassis = list(interface[ifname]['chassis'].keys())[0]

    vlan = "NoVLAN"
    port = "UnknownPort"
    portDescription = "none"

    try:
      vlan = interface[ifname]['vlan']['vlan-id']
    except:
      pass
    try:
      port = interface[ifname]['port']['id']['value']
    except:
      pass
    try:
      portDescription = interface[ifname]['port']['descr']
    except:
      pass

    if port == portDescription:
      portDescription = "none"

    ## Get Adaptater details from udevadm
    ## udevadm info /sys/class/net/eno1 | grep ID_MODEL_FROM_DATABASE | cut -d '=' -f2
    adaptaterName = None
    adaptaterInfosProcess = subprocess.run(['udevadm', 'info', '/sys/class/net/' + ifname], capture_output=True, text=True)
    adaptaterInfos = adaptaterInfosProcess.stdout

    if adaptaterInfosProcess.returncode != 0:
      adaptaterName = "udevadm error"
    else:
      ## udevadm has too much information, we only need one
      for item in adaptaterInfos.split("\n"):
        if "ID_MODEL_FROM_DATABASE" in item:
          adaptaterName = item.strip().split("=")[1]

      if not adaptaterName:
        adaptaterName = "Unable to Identify Model"

    print(today_date, chassis, vlan, port, portDescription, adaptaterName, ifname, sep='|', flush=True, file=f)
    #output=output + chassis + "|" + vlan + "|" + port + "|" + portDescription + "|" + adaptaterName + "|" + ifname + "\n"

except Exception as error:
  print("Error parsing LLDP response:", error, file=f)
  exit (1)