# Windows átvételi ellenőrzőlista (valódi szerver)

Ezeket a pontokat csak a valódi Windows-szerveren lehet ellenőrizni (Ollama + Qwen 3.5 9B, Thunderbird, szolgáltatás,
hálózati megosztás, telepítő). A felhős fejlesztőkörnyezetben ezek helyett mock és szimuláció fut (`docs/DEV_MODE.md`).
Minden lépés a saját pontjaival bővíti a listát.

A buildelt `EfMailAgent.exe` a GitHub Actions „build” futásának `EfMailAgent-win-x64` artefaktjaként tölthető le.

## 1. lépés: alapváz, beállítások, szabályok

- [ ] `EfMailAgent.exe validate-rules` (paraméter nélkül, Release build) létrehozza a `C:\ProgramData\EfMailAgent\`
      mappát a `rules`, `output`, `data`, `logs`, `addon` almappákkal és az `appsettings.json` fájllal.
- [ ] Az `appsettings.json` szolgáltatója `ollama`, a `Server.Token` 43 karakteres véletlen érték.
- [ ] Ha a `rules` mappa üres volt, és az exe mellett van `samples\rules` mappa, a mintaszabályok átmásolódnak
      (telepítés előtt ezt a telepítő végzi, lásd 7. lépés).
- [ ] `EfMailAgent.exe print-token` csak a tokent írja ki.
- [ ] A magyar ékezetek helyesen jelennek meg a parancssorban (cmd és PowerShell is), és a naplófájlban
      (`logs\efmailagent-ÉÉÉÉHHNN.log`).
- [ ] `EfMailAgent.exe validate-rules --watch` mellett a `rule.yaml` Jegyzettömbben való módosítása és mentése után
      kb. 1 másodpercen belül újra megjelenik a táblázat, új verziószámmal.
- [ ] Egy szándékosan elrontott `rule.yaml` (pl. `type: decimal`) magyar hibaüzenetet ad, a többi szabály érvényes
      marad, a kilépési kód 1.
