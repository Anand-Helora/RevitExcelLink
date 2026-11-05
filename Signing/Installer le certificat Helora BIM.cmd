@echo off
chcp 65001 >nul
echo.
echo  Helora BIM - installation du certificat de lediteur
echo  ---------------------------------------------------
echo  Revit reconnaitra ensuite ladd-in comme provenant de "Helora BIM"
echo  et ne posera plus de question au demarrage.
echo  Aucun droit administrateur nest necessaire.
echo.
certutil -user -addstore TrustedPublisher "%~dp0HeloraBIM.cer" >nul
if errorlevel 1 (echo  [!] Impossible dajouter lediteur de confiance. & goto fin)
echo  [OK] Editeur de confiance : Helora BIM
echo.
echo  Windows va demander de confirmer le certificat : repondez OUI.
certutil -user -addstore Root "%~dp0HeloraBIM.cer" >nul
if errorlevel 1 (echo  [!] Certificat racine refuse : Revit demandera une derniere fois, cliquez "Toujours charger". & goto fin)
echo  [OK] Certificat installe.
:fin
echo.
pause
