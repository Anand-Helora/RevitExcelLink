# Helora BIM – Excel Link

Add-in Revit d'export / import Excel. Au démarrage, Revit affiche une page de garde Helora BIM pendant 3 secondes (un clic la ferme).

Interface et classeurs aux couleurs de la charte Helora : vert foncé #044550, turquoise #009E99 et ses nuances, orange #E16021 réservé aux signaux. La police Gotham Rounded est utilisée si elle est installée, sinon Segoe UI.

Add-in personnel pour Revit 2025, 2026 et 2027 (une seule DLL compilée en .NET 8).
Il ajoute un onglet **Helora BIM** au ruban (groupe **Excel Link**), avec deux boutons : **Exporter vers Excel** et **Importer depuis Excel**.

## 1. Compiler et installer

Prérequis : Windows, Visual Studio 2022 (charge de travail « Développement .NET Desktop ») ou le SDK .NET 8.

1. Fermez Revit (il verrouille la DLL quand il est ouvert).
2. Ouvrez `RevitExcelLink.csproj` dans Visual Studio, ou lancez dans le dossier du projet :
   ```
   dotnet build -c Release
   ```
3. Le projet se déploie seul dans `%AppData%\Autodesk\Revit\Addins\2025\` :
   - `RevitExcelLink.addin`
   - `RevitExcelLink\` (la DLL et ses dépendances ClosedXML)
4. Démarrez Revit et acceptez le message de sécurité (« Toujours charger »).

Pour installer dans plusieurs versions de Revit, choisissez-les au moment de la compilation :
```
dotnet build -c Release -p:DeployRevitVersions="2025;2026;2027"
```

Les références à l'API Revit viennent des paquets NuGet `Nice3point.Revit.Api.*`. Il n'est pas nécessaire que Revit soit installé sur la machine qui compile.

## 2. Utilisation

### Export
1. **Portée** : tout le modèle, la vue active ou la sélection courante. La sélection est choisie par défaut si des éléments sont sélectionnés dans Revit.
2. **Catégories** : cochez celles à exporter. Une feuille Excel est créée par catégorie.
3. **Paramètres** : faites-les passer à droite avec → ou un double-clic, puis réglez l'ordre des colonnes avec ↑ ↓.
   Vert = occurrence, jaune = type, rouge = lecture seule.
4. **« Une ligne par type »** : une ligne par type au lieu d'une ligne par élément, avec les paramètres de type uniquement.
5. **Profils** : enregistrent et rechargent une sélection (portée, catégories, colonnes). Ils sont rangés dans `%AppData%\RevitExcelLink\Profils`.

### Export des nomenclatures (onglet « Nomenclatures »)
- Toutes les nomenclatures du projet sont listées : quantitatifs, métrés de matériaux, listes de feuilles et de vues, nomenclatures de clés. Si une nomenclature est ouverte dans Revit, elle est cochée automatiquement.
- **Modifiable** : une ligne par élément affiché (avec les filtres de la nomenclature), les colonnes et leurs en-têtes repris de la nomenclature, dans le même ordre de tri. Le fichier se **réimporte** comme un export de catégories.
  Les champs d'éléments liés (Pièce de départ, Pièce d'arrivée, Pièce, Espace, Informations sur le projet) sont exportés et modifiables : la modification s'applique à la pièce, à l'espace ou au projet, une seule fois même s'ils apparaissent sur plusieurs lignes.
  Les colonnes calculées (formules, totaux, nombre, pourcentages) ne sont pas modifiables : elles ne sont pas exportées dans ce mode.
- **Mise en forme** : copie fidèle de l'affichage Revit (titre, en-têtes, regroupements, totaux, cellules fusionnées, largeurs de colonnes). Ce fichier n'est **pas réimportable**.
- Un aperçu des colonnes exportées s'affiche quand on clique sur une nomenclature.

### Le fichier Excel
- Un onglet **Instructions** contient la légende et les règles.
- Les nombres sont de vrais nombres, dans l'unité d'affichage du projet indiquée dans l'en-tête, par exemple `Hauteur [mm]`.
- Oui/Non, niveaux, phases, matériaux et autres éléments liés se choisissent dans une **liste déroulante**.
- Vous pouvez **trier, filtrer, déplacer des colonnes** et utiliser des formules.

### Import
1. Enregistrez le fichier dans Excel. Il peut rester ouvert.
2. Cliquez sur **Importer depuis Excel** et choisissez le fichier.
3. Un **aperçu** liste chaque modification : valeur actuelle, nouvelle valeur et statut.
   - **Modification** : cochée par défaut.
   - **Conflit** : la valeur a aussi changé dans Revit depuis l'export. Elle est décochée par défaut ; cochez-la pour imposer la valeur d'Excel.
   - **Lecture seule / Erreur / Introuvable** : rien ne sera appliqué, la raison est indiquée.
4. **Appliquer** : tout se fait en une seule transaction, annulable d'un seul **Ctrl+Z**.
   Un rapport `.txt` est écrit à côté du fichier Excel.

## 3. Choix de conception (pour limiter les problèmes)

| Problème | Solution retenue |
|---|---|
| Écraser une valeur modifiée dans Revit après l'export | Un **instantané** masqué des valeurs exportées est gardé dans le fichier. Seules les cellules que vous avez modifiées sont importées, et les conflits sont signalés. |
| Unités | Les nombres sont convertis dans l'unité d'affichage du projet. L'unité utilisée est **enregistrée dans le fichier**, si bien que l'import reste juste même si les unités du projet changent entre-temps. À l'import, on accepte aussi `2500,5`, `2 500` ou `2500 mm`, et en dernier recours le format de Revit (`1' 6"`). |
| Identifier les éléments | Par l'**UniqueId**, qui ne change pas, plutôt que par l'ElementId. Les lignes peuvent être triées librement. |
| Identifier les paramètres | Par le GUID pour un paramètre partagé, par l'identifiant interne pour un paramètre Revit, et par le nom sinon. Cela ne dépend pas de la langue de Revit. |
| Paramètres de type sur des lignes d'occurrence | Ils sont regroupés en une seule modification par type. Si des valeurs différentes ont été saisies pour le même type, c'est une erreur. |
| Zéros en tête, valeurs commençant par « = » | Les colonnes de texte sont au format Texte. |
| Fichier ouvert dans Excel | La lecture se fait en mode partagé. |
| Avertissements Revit pendant l'import | Ils sont supprimés automatiquement. Les vraies erreurs restent affichées par Revit. |
| Travail partagé | Un élément réservé par un autre utilisateur est ignoré, avec un message. |
| Conflits de DLL avec d'autres add-ins | Revit 2027 isole les dépendances de chaque add-in. Dans Revit 2025 et 2026, un conflit reste possible si un autre add-in charge une autre version d'OpenXML. Le symptôme est une erreur à l'export ou à l'import, et la solution est de mettre à jour l'un des deux add-ins. |

## 4. Limites de cette v1
- Pas de création ni de suppression d'éléments. Les lignes ajoutées sont ignorées, avec un avertissement.
- Pas de création de pièces et espaces, de modèles liés, ni de Google ou Morta.
- Les paramètres entiers de type « énumération » (sous-projet, par exemple) sont exportés sous forme de nombre.
- Changer le **type** d'un élément (paramètre « Type ») n'est pas pris en charge : ce paramètre est en lecture seule.
- Liste déroulante des éléments liés : elle n'est créée que si au moins un élément de la colonne a déjà une valeur, ce qui permet de savoir s'il s'agit de niveaux, de matériaux, etc.

## 5. Structure du code

```
App.cs                       Ruban (onglet + 2 boutons)
Commands/ExportCommand.cs    Bouton Exporter
Commands/ImportCommand.cs    Bouton Importer
Core/ModelScanner.cs         Catégories et paramètres disponibles
Core/ParamModel.cs           Identification stable des paramètres
Core/ValueCodec.cs           Conversion Revit <-> Excel (unités, Oui/Non, éléments liés)
Core/ElementNameLookup.cs    Nom -> ElementId (niveaux, matériaux…)
Core/SheetLayout.cs          Organisation du classeur, couleurs
Core/ExcelExporter.cs        Écriture du .xlsx
Core/ExcelImporter.cs        Lecture et détection des modifications
Core/ChangeApplier.cs        Application dans Revit et rapport
Core/ExportProfile.cs        Profils JSON
Core/ScheduleScanner.cs      Nomenclatures (modifiable et mise en forme)
UI/Theme.xaml                Thème Helora (couleurs, boutons, onglets)
UI/ExportWindow.xaml(.cs)    Fenêtre d'export
UI/ImportPreviewWindow…      Aperçu de l'import
UI/Icons.cs                  Icônes dessinées en code
```

## 6. Plan de test conseillé (sur une copie de projet)
1. Exportez les **Murs** avec Commentaires, Marque, Hauteur non contrainte et Contrainte inférieure (niveau).
2. Dans Excel, modifiez un commentaire, une hauteur (en mm) et un niveau (via la liste). Enregistrez.
3. Importez : l'aperçu doit montrer exactement ces 3 modifications. Appliquez, vérifiez dans Revit, puis faites Ctrl+Z.
4. Testez un conflit : exportez, modifiez une valeur dans Revit et la même dans Excel, puis importez. Le conflit doit apparaître décoché.
5. Testez un paramètre de type (Largeur d'une porte) en mode « une ligne par élément », puis en mode « une ligne par type ».
6. Testez une saisie invalide (texte dans une colonne de longueur) : elle doit apparaître en erreur.
