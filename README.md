# SWTool

A small Windows Forms monitor for mobile broadband modems. It talks to the modem's AT command port, so it
shows what Windows' own settings do not. Any modem with an AT port gets the standard 3GPP information; two
command dialects are known in detail:

| Modem type | Tested with | Extra detail |
| --- | --- | --- |
| Sierra Wireless (`AT!...`) | EM7455 (LTE) | RSRP / RSRQ / SINR per antenna, band, bandwidth, channel, TAC, eNodeB/sector, PCI, LTE neighbour cells, temperature, network type and band presets |
| Icera based sticks (`AT%...`) | Nokia 7M-01 (2G/3G) | GPRS / EDGE / HSPA state, level in dBm, bit error class, LAC, routing area, cell ID, measured cells, home network, firmware parts, network type preference and individual band switches |

The tabs:

- **Status** - operator, network type, roaming state, signal values with a rating, cell identity, IP
  addresses and a 10 minute signal history.
- **Cells** - the serving cell and the other cells the modem measures.
- **Networks** - network search grouped by network type (LTE / UMTS / GSM), manual registration on a chosen
  network (optionally locked to its type), switching back to automatic selection, and the modem's network
  type and band preferences.
- **Details** - device and SIM identifiers plus everything the modem's status commands report.
- **AT console** - send any AT command yourself.

## Build

Needs the Visual Studio 2015 build tools (MSBuild 14, C# 6) and the .NET Framework 4.6.1 targeting pack.

```
build.cmd
```

The result is `SWTool\bin\Release\SWTool.exe`, a single file with no dependencies besides .NET 4.6.1 or later.

## Notes

- The AT port is a COM port: the "WWAN Modem" device of a Sierra card, the "Wireless AT Device" of the Nokia
  stick. Candidates are listed with the most likely one first; only one program can have a port open at a
  time. The modem keeps working through Windows while SWTool is connected.
- A network search takes 30 seconds to 2 minutes, can pause data transfer and cannot be interrupted.
- Manual registration and the network type and band preferences are stored in the modem and stay in effect
  after the tool is closed.
- A modem that does not answer is treated as busy, not as gone: SWTool sends nothing further (it would only
  queue up), shows which command it is waiting for and for how long, and carries on when the answer arrives.
  The port is only reopened when it really disappears. The AT console waits 15 seconds at most; a later
  answer is printed when it comes.
- **Restart modem** is the software equivalent of unplugging it (`AT%IRESET`, `AT!RESET` or `AT+CFUN=1,1`
  depending on the modem). It is sent on the connected port if that still answers, otherwise on another
  AT port of the same device, and SWTool reconnects when the modem is back.
- No administrator rights are needed. The port, refresh interval and the "lock" option are remembered under
  `HKCU\Software\SWTool`.

### Sierra Wireless (EM7455)

- A manual registration always rewrites the modem's network type preference: `AT+COPS=1,2,"plmn",act` sets
  it to that type only, `AT+COPS=1,2,"plmn"` resets it to Automatic. "Lock to its network type" chooses
  between the two; the "Network type" preference changes it afterwards.
- Firmware quirk: while the preference is locked to one type, naming that type again for a *different*
  network is answered with "registration denied" and the command never returns. SWTool therefore always
  selects the operator first without a type and locks the type in a second step.
- A network that refuses the SIM never answers the registration command. SWTool ends the attempt after
  100 seconds (any input makes the modem drop it), and "Cancel registration" does the same earlier; the
  modem then returns to the network it was on.
- Setting the two preferences sends Sierra's `AT!ENTERCND="A710"` unlock first.

### Icera (Nokia 7M-01)

- Status comes from `%NWSTATE`, `%ICSQ`, `%INCELL`, `%IPSYS`, `%IPBM` and the location fields of
  `+CREG=2` / `+CGREG=2`. The modem reports no channel numbers, and `%INCELL` gives a cell ID only once the
  modem has read that cell's broadcast information.
- Network type preference is `AT%IPSYS=<mode>,<domain>`; changing it makes the modem re-register (a few
  seconds without service). The 7M-01 answers OK to "GSM preferred" but keeps its old setting, so SWTool
  reads the value back and reports that.
- Bands are switched one by one with `AT%IPBM="<band>",0|1`, which also makes the modem re-register.
- A manual registration does not change the network type preference on this modem.
- A registration blocks the AT port it was sent on until the modem is done, up to about 100 seconds for a
  network that refuses the SIM, and nothing on that port interrupts it (input, DTR and break are ignored).
  SWTool waits and shows the elapsed time; if the registration fails it puts the previous selection back.
- `AT%INCELL?` must not be sent while the modem has no serving cell: it then starts a measurement that
  blocks the port for over a minute and keeps the radio from finding a network, and repeating it locks the
  modem out until it is unplugged. SWTool only asks for the cell list once registration has held for two
  polls.
- The other AT ports keep answering while one is blocked, but forcing things from there is not safe:
  `AT+CFUN=4` sent during a registration left that port dead and the modem without service until a restart.

## Source layout

| File | Purpose |
| --- | --- |
| `AtChannel.cs` | Serial port access, one AT command at a time |
| `ModemPorts.cs` | Finds candidate AT ports via WMI |
| `Modem.cs` | Modem type detection, polling and the scan / register / preference operations |
| `Parsers.cs` | Parsers for the Sierra, Icera and standard command answers, and the LTE band table |
| `Model.cs` | Status snapshot and signal rating thresholds |
| `Controls.cs` | Signal bars, meters, history chart, flicker free list view |
| `MainForm.cs` | The window |
