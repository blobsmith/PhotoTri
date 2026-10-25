# Cahier des charges — Transfert et classement de photos Android → PC

Version 2 — 25/10/2026

## 1. Objectif

Une application Windows qui transfère par câble USB les photos et les vidéos d'un ou plusieurs téléphones Android vers le PC ou vers un disque dur externe, puis les classe par **date**, par **personne** et par **lieu**. À chaque transfert, l'utilisateur choisit s'il garde les fichiers sur le téléphone ou s'il les supprime.

Profil : un seul utilisateur, une seule bibliothèque, alimentée par plusieurs téléphones (par exemple ceux de la famille).

## 2. Composants

Une seule **application Windows**, qui assure le transfert, le classement, la visionneuse, la reconnaissance faciale et les réglages. **Aucune application n'est à installer sur le téléphone.**

## 3. Connexion

- **USB uniquement (MTP)** : le téléphone est branché en mode « Transfert de fichiers ». À la première connexion, il faut déverrouiller le téléphone et accepter l'accès.
- La suppression des fichiers sur le téléphone passe elle aussi par la connexion USB.
- Chaque téléphone est reconnu automatiquement et peut être nommé (par exemple « Téléphone de Paul »). Ce nom sert ensuite de filtre.

## 4. Fichiers concernés

- Photos de l'appareil photo (JPG, HEIC, PNG)
- Vidéos (MP4 et autres formats courants)
- Captures d'écran
- Photos et vidéos reçues dans WhatsApp, Messenger et les autres apps (dossiers connus, plus des dossiers ajoutés par l'utilisateur)

Pour chaque type, une case à cocher dans les réglages permet de l'inclure ou de l'exclure.

## 5. Transfert

1. L'application détecte le téléphone branché en USB et vérifie que le disque de destination est disponible (voir §10).
2. Elle liste les fichiers **nouveaux**. Ceux qui ont déjà été transférés sont ignorés automatiquement : l'application garde une mémoire des empreintes de fichiers.
3. L'utilisateur choisit **Garder sur le téléphone** ou **Supprimer du téléphone**.
4. Copie avec barre de progression. Le transfert peut reprendre après une interruption.
5. **Vérification** de chaque copie (taille et empreinte SHA-256).
6. Si l'utilisateur a choisi « Supprimer », seuls les fichiers dont la copie est vérifiée sont supprimés du téléphone. Un fichier dont la copie a échoué n'est jamais supprimé.
7. Un résumé s'affiche : X fichiers copiés, Y ignorés (doublons), Z supprimés du téléphone, avec les erreurs éventuelles.

## 6. Classement

### 6.1 Rangement physique (Explorateur Windows)

```
<Bibliothèque>\
  2026\
    2026-10 Octobre\
      20261024_143012.jpg
      ...
```

- La date vient des données EXIF de prise de vue. À défaut, on utilise la date indiquée dans le nom du fichier, puis la date de modification.
- Les fichiers de toutes origines sont rangés dans la même arborescence.
- Le dossier de la bibliothèque se choisit dans les réglages : sur le PC (par défaut `Images\Photos téléphone`) ou sur un disque dur externe (voir §10).

### 6.2 Vues virtuelles (dans l'application, sans dupliquer les fichiers)

- **Dates** : chronologie par année, puis par mois, puis par jour.
- **Personnes** : un album par personne reconnue.
- **Lieux** : un album par ville, avec une carte des photos.
- **Filtres** combinables : origine (appareil photo, WhatsApp, captures d'écran…), téléphone, type (photo ou vidéo), personne, lieu, période.

## 7. Reconnaissance des personnes

- Détection et reconnaissance faciale **automatiques, 100 % en local** sur le PC (aucun envoi sur Internet).
- Les visages qui se ressemblent sont regroupés. L'utilisateur nomme chaque groupe **une seule fois**, et les photos suivantes sont attribuées automatiquement.
- Fusion de deux groupes appartenant à la même personne, et retrait d'un visage mal attribué.
- Le traitement se fait en arrière-plan après le transfert. L'application reste utilisable pendant ce temps.
- Les vidéos ne passent pas par la reconnaissance faciale.

## 8. Lieux

- Les coordonnées viennent du GPS des photos (EXIF) et des vidéos (métadonnées).
- **Hors ligne** : une base intégrée (GeoNames, communes de plus de 500 habitants, noms en français) convertit les coordonnées en *ville, région, pays*. C'est une estimation : la commune dont le centre est le plus proche.
- **En ligne (facultatif, désactivé par défaut)** : OpenStreetMap/Nominatim donne l'adresse précise (rue, quartier, lieu-dit) et la **commune exacte**, qui remplace l'estimation hors ligne pour le classement. Le service est interrogé au maximum une fois par seconde, une seule fois par position arrondie à environ 100 m.
- Les photos sans GPS sont regroupées sous « Lieu inconnu ».

## 9. Interface Windows

- Fenêtre principale avec trois onglets (Dates, Personnes, Lieux), une grille de miniatures et une visionneuse plein écran.
- Bouton **Transférer** avec une liste des téléphones branchés.
- Indicateur de la destination (PC ou disque externe) avec l'espace libre restant.
- Écran **Personnes à nommer**.
- Carte des lieux.
- Réglages : dossier de la bibliothèque (PC ou disque externe), types de fichiers, géocodage en ligne, noms des téléphones.
- Interface en français.

## 10. Disque dur externe

- La bibliothèque peut être placée sur un disque dur externe (USB) au lieu du PC.
- Le disque est reconnu par son identifiant de volume et non par sa lettre (E:, F:…). L'application le retrouve même si Windows lui attribue une autre lettre.
- Si le disque n'est pas branché, l'application le signale clairement et bloque le transfert. Les vues déjà indexées restent consultables, avec leurs miniatures.
- L'espace libre est vérifié avant chaque transfert. S'il est insuffisant, le transfert ne démarre pas.
- Si le disque est débranché pendant le transfert, les fichiers déjà copiés et vérifiés sont conservés et **aucune suppression n'a lieu sur le téléphone** pour les autres. Le transfert reprend au prochain branchement.
- La base de données est stockée sur le disque externe, avec les photos. On peut donc brancher le disque sur un autre PC équipé de l'application et retrouver toute la bibliothèque.
- Le PC garde une copie de la base et les miniatures (dans `%LocalAppData%\PhotoTri`). C'est ce qui permet de parcourir la bibliothèque quand le disque est débranché. Sur un autre PC, les miniatures sont recréées à la première ouverture.

## 11. Exigences générales

- Aucune perte de données : jamais de suppression sur le téléphone sans copie vérifiée, et jamais d'écrasement d'un fichier existant sur le PC.
- Les données (personnes, lieux, index) sont stockées dans une base locale (SQLite) à l'intérieur de la bibliothèque. La bibliothèque peut ainsi être déplacée avec ses données.
- Windows 10 et 11, 64 bits.

## 12. Choix techniques proposés

| Partie | Technologie |
|---|---|
| Application PC | C# / .NET 8, WPF |
| USB | API Windows Portable Devices (MTP) |
| Reconnaissance faciale | Modèles ONNX (détection + empreinte de visage) via ONNX Runtime |
| Géocodage hors ligne | Base GeoNames des villes de plus de 1 000 habitants |
| Géocodage en ligne | Nominatim (OpenStreetMap) |
| Base de données | SQLite |

## 13. Hors périmètre (version 1)

- iPhone
- Transfert par Wi-Fi et application Android
- Synchronisation dans le cloud
- Retouche des photos
- Reconnaissance faciale dans les vidéos
