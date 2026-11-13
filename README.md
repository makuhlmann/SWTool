# SWTool

A small Windows Forms monitor for Sierra Wireless WWAN cards (written against an EM7455, should work with
other MC/EM73xx/74xx/75xx modules). It talks to the card's AT command port, so it shows what Windows' own
settings do not:

- **Status** - operator, network type, band and bandwidth, roaming state, RSRP / RSRQ / SINR / RSSI with a
  rating, per-antenna levels, cell IDs (TAC, cell ID, eNodeB/sector, PCI), IP addresses, temperature and a
  10 minute signal history.
- **Cells** - the serving cell and the neighbour cells the modem measures.
- **Networks** - network search grouped by network type (LTE / UMTS / GSM), manual registration on a chosen
  network and type, switching back to automatic selection, and the modem's network type and band preferences.
- **Details** - device and SIM identifiers plus everything `AT!GSTATUS?` reports.
- **AT console** - send any AT command yourself.

## Build

Needs the Visual Studio 2015 build tools (MSBuild 14, C# 6) and the .NET Framework 4.6.1 targeting pack.

```
build.cmd
```

The result is `SWTool\bin\Release\SWTool.exe`, a single file with no dependencies besides .NET 4.6.1 or later.

## Notes

- The AT port is the card's "WWAN Modem" device (a COM port). It is detected automatically; only one program
  can have it open at a time. The card keeps working through Windows (MBIM) while SWTool is connected.
- A network search takes 30 seconds to 2 minutes and can pause data transfer. Disconnect aborts it; the modem
  may then need a few seconds before it answers again.
- Registering on a network with a given type (`AT+COPS=1,2,"plmn",act`) also sets the modem's network type
  preference to that type only (seen on the EM7455: "UMTS and LTE Only" became "LTE Only"). Widen it again
  with the "Network type" preference on the Networks tab.
- Manual registration, the network type preference and the band preference are stored in the modem and stay
  in effect after the tool is closed. Setting the two preferences sends Sierra's `AT!ENTERCND="A710"` unlock
  first.
- No administrator rights are needed. The port and refresh interval are remembered under
  `HKCU\Software\SWTool`.

## Source layout

| File | Purpose |
| --- | --- |
| `AtChannel.cs` | Serial port access, one AT command at a time |
| `ModemPorts.cs` | Finds candidate AT ports via WMI |
| `Modem.cs` | Polling and the scan / register / preference operations |
| `Parsers.cs` | Parsers for `!GSTATUS`, `!LTEINFO`, `+COPS`, `+CESQ`, ... and the LTE band table |
| `Model.cs` | Status snapshot and signal rating thresholds |
| `Controls.cs` | Signal bars, meters, history chart, flicker free list view |
| `MainForm.cs` | The window |
