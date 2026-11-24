# Logi Plugin Backup

Sauvegarde et restaure **tous les plugins installés de Logi Options+** (MX Creative Keypad / Dialpad) et leur
configuration : profils et dispositions des touches, données des plugins, médias, réglages du service.
Application WPF .NET 10, portable (un seul `.exe`), **sans droits administrateur**.

> Le projet voisin `LogiOptionsBackupManager` sauvegarde les réglages de Logi Options+ lui-même (compte, souris,
> clavier). Celui-ci s'occupe des **plugins et de leurs profils**. Les deux se complètent.

## Utilisation

Lancer `publish\LogiPluginBackup.exe`.

**Sauvegarder** : cochez les plugins et les jeux de configuration, choisissez le fichier `.lpbak`, cliquez
*Sauvegarder*. Logi Options+ peut rester ouvert (les fichiers sont lus en lecture partagée).

**Restaurer** : *Ouvrir une sauvegarde...*, la liste montre pour chaque plugin la version sauvegardée, la version
installée ici et un état (absent, même version, version différente). *Cocher ce qui diffère* sélectionne l'utile.
*Vérifier l'intégrité* recalcule les SHA-256. Au clic sur *Restaurer* :

1. l'archive est vérifiée (une archive modifiée est refusée avant toute écriture) ;
2. ce qui va être remplacé est copié dans `%LOCALAPPDATA%\LogiPluginBackup\pre-restore\<date>` ;
3. le service de plugins Logi est arrêté (processus utilisateur) ; Options+ le relance seul en quelques secondes ;
4. les dossiers sont remplacés (supprimés puis extraits : aucun fichier périmé ne reste) ;
5. l'application attend 30 s que le service reparte ; sinon elle demande de relancer Options+.

## Ligne de commande

Avec des arguments, l'exe fonctionne en console (sauvegardes planifiées, scripts). Code de sortie : 0 réussite,
1 erreur, 2 arguments invalides.

```powershell
$exe = 'D:\MXCreativKeydad\LogiPluginBackup\publish\LogiPluginBackup.exe'
Start-Process $exe -ArgumentList 'list' -Wait -NoNewWindow
Start-Process $exe -ArgumentList 'backup', "$env:USERPROFILE\Documents\plugins.lpbak" -Wait -NoNewWindow
Start-Process $exe -ArgumentList 'restore', "$env:USERPROFILE\Documents\plugins.lpbak", '--yes' -Wait -NoNewWindow
```

Options : `--no-config` (plugins seulement), `--no-stop` (ne pas arrêter le service), `--yes` (obligatoire pour restaurer).

## Ce qui est sauvegardé

| Élément | Détail |
|---|---|
| Plugins installés | un dossier par plugin dans `%LOCALAPPDATA%\Logi\LogiPluginService\Plugins` (Marketplace, `.lplug4`) |
| Plugins en développement | un fichier `.link` désigne un dossier de build : ce dossier est sauvegardé comme un plugin normal |
| Profils et touches | `Applications\` (un dossier par appareil et par application) |
| Données des plugins | `PluginData\` |
| Médias | `Media\` |
| Réglages du service | `LoupedeckSettings.ini` |
| Option : copies automatiques | `Applications.Backups\` (volumineux, décoché par défaut) |

**Jamais sauvegardé** : `Logs`, `Temp`, `PluginHosts` (caches reconstruits par le service), compte Logitech, réglages
d'Options+ (voir `LogiOptionsBackupManager`).

## Format `.lpbak` (zip standard)

```
manifest.json                  format lpbak/1, date, PC, versions, SHA-256 de chaque fichier
plugins/<dossier>/...          même arborescence que dans Plugins\
config/<nom>/...               Applications, PluginData, Media
config/LoupedeckSettings.ini
```

## Garde-fous

* Chemins de l'archive contrôlés (aucun `..`, aucun chemin absolu : pas de « zip slip »).
* Empreinte SHA-256 de chaque fichier, vérifiée avant toute restauration.
* Un plugin déjà chargé sur ce PC par un lien de développement n'est pas restauré (il serait chargé deux fois).
* Si le service ne peut pas être arrêté, rien n'est modifié.
* Les fichiers verrouillés pendant la sauvegarde sont ignorés et signalés ; les autres sont sauvegardés.

## Développement

```powershell
dotnet test
dotnet publish src\LogiPluginBackup.App -c Release -o publish
```

* `src/LogiPluginBackup.Core` : inventaire, sauvegarde, restauration, contrôle du service (aucune dépendance).
* `src/LogiPluginBackup.App` : interface WPF et mode console.
* `tests/LogiPluginBackup.Core.Tests` : 10 tests xUnit, tous dans des dossiers temporaires (jamais l'installation réelle).
* Variable `LOGIPLUGINBACKUP_ROOT` : remplace le dossier du service, pour essayer une restauration sans risque.

## Limites

* La restauration sur un **autre PC** copie les profils tels quels : si le nom d'utilisateur Windows diffère, les chemins
  absolus enregistrés dans certains profils ne sont pas réécrits.
* Que le service de plugins reparte tout seul après l'arrêt est constaté sur Options+ 2.9 / Plugin Service 6.4 ;
  sans cela, relancer Logi Options+.
* La restauration dans l'installation réelle n'a pas été exécutée : elle a été testée en dossier temporaire
  (sauvegarde réelle de ce PC restaurée vers un dossier vide : mêmes fichiers, mêmes empreintes).
