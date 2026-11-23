/* HostingPanel page catalog: UI-only DirectAdmin-inspired screens.
   All data is synthetic and no API calls or persistent writes occur. */
(function () {
"use strict";
const F=(label,type="text",value="",options=[])=>({label,type,value,options});
const L=(columns,rows=[],actions=["Toevoegen"])=>({kind:"list",columns,rows,actions});
const P=(fields,actions=["Opslaan","Annuleren"])=>({kind:"form",fields,actions});
const S=(tabs,fields,actions=["Opslaan"])=>({kind:"settings",tabs,fields,actions});
const M=(columns,rows)=>({kind:"monitor",columns,rows,actions:["Vernieuwen"]});
const select=(label,options,value="")=>F(label,"select",value,options);
const check=(label,value="Aan")=>F(label,"check",value);
const textarea=(label,value="")=>F(label,"textarea",value);
const accounts=[["demo","Gebruiker","Actief","basic","example.nl"],["hosting","Gebruiker","Actief","business","demo.example.nl"],["admin","Admin","Actief","onbeperkt","—"]];
const packages=[["basic","5 GB","50 GB","5","10"],["business","20 GB","200 GB","25","100"],["unlimited","Onbeperkt","Onbeperkt","Onbeperkt","Onbeperkt"]];
const hosts=[["example.nl","demo","Actief","192.0.2.10"],["demo.example.nl","hosting","Actief","192.0.2.11"]];
const optsYN=["Ja","Nee"];
const catalog={
"Nieuwe Gebruiker Toevoegen":P([F("Gebruikersnaam"),F("E-mailadres","email"),F("Wachtwoord","password"),select("Gebruikerspakket",["basic","business","unlimited"]),select("Domein",["Nieuw domein","Bestaand domein"]),F("Domeinnaam"),select("IP-adres",["Gedeeld","Dedicated"]),check("Stuur accountgegevens per e-mail")],["Aanmaken","Annuleren"]),
"Toon Alle Gebruikers":L(["Gebruiker","Niveau","Status","Pakket","Domein"],accounts,["Nieuwe gebruiker","Filter op niveau","Bericht versturen"]),
"Mijn Gebruikers":L(["Gebruiker","Niveau","Status","Pakket","Domein"],accounts.slice(0,2),["Nieuwe gebruiker","Verwijderen"]),
"Beheer gebruikers pakketten":L(["Pakket","Schijfruimte","Bandbreedte","Domeinen","E-mailaccounts"],packages,["Nieuw pakket","Pakket aanpassen","Verwijderen"]),
"Verplaats Gebruikers tussen Resellers":P([select("Gebruiker",["demo","hosting"]),select("Van reseller",["admin","reseller-demo"]),select("Naar reseller",["reseller-demo","admin"])],["Gebruiker verplaatsen"]),
"Berichtsjablonen":L(["Sjabloon","Onderwerp","Taal","Status"],[["account-created","Nieuw hostingaccount","Nederlands","Actief"],["password-reset","Wachtwoord gewijzigd","Nederlands","Actief"],["suspension","Account geblokkeerd","Nederlands","Actief"]],["Nieuw sjabloon","Bewerk sjabloon"]),
"Wachtwoorden wijzigen":P([select("Account",["admin","demo","hosting"]),F("Nieuw wachtwoord","password"),F("Bevestig wachtwoord","password"),check("Gebruiker informeren")],["Wachtwoord aanpassen"]),
"Reseller Aanmaken":P([F("Gebruikersnaam"),F("E-mailadres","email"),F("Wachtwoord","password"),select("Resellerpakket",["reseller-basic","reseller-business"]),F("Domeinnaam"),check("Welkomstbericht sturen")],["Reseller aanmaken"]),
"Resellers":L(["Reseller","Pakket","Gebruikers","Domeinen","Status"],[["reseller-demo","reseller-business","4","9","Actief"]],["Reseller aanmaken","Schorsen"]),
"Beheer Reseller Pakketten":L(["Pakket","Schijfruimte","Bandbreedte","Gebruikers","Domeinen"],[["reseller-basic","50 GB","500 GB","10","50"],["reseller-business","200 GB","2 TB","50","200"]],["Nieuw pakket"]),
"Admin Aanmaken":P([F("Gebruikersnaam"),F("E-mailadres","email"),F("Wachtwoord","password"),F("Bevestig wachtwoord","password"),check("Stuur accountgegevens")],["Administrator aanmaken"]),
"Administrators":L(["Gebruiker","E-mailadres","Status","Laatst ingelogd"],[["admin","admin@example.nl","Actief","Vandaag"],["support","support@example.nl","Actief","Gisteren"]],["Admin aanmaken"]),
"Administrator instellingen":S(["Algemeen","Beveiliging","E-mail","Gebruikers","Geavanceerd"],{
"Algemeen":[F("Servernaam","text","hosting.example.nl"),F("Administrator e-mailadres","email","admin@example.nl"),select("Standaardtaal",["Nederlands","English"],"Nederlands"),select("Tijdzone",["Europe/Amsterdam","UTC"],"Europe/Amsterdam")],
"Beveiliging":[check("Tweefactorauthenticatie afdwingen"),F("Maximaal aantal mislukte aanmeldingen","number","5"),check("IP-whitelist inschakelen","Uit"),F("Sessie-timeout (minuten)","number","30")],
"E-mail":[F("SMTP-host","text","mail.example.nl"),F("SMTP-poort","number","587"),select("Transportbeveiliging",["STARTTLS","TLS","Geen"],"STARTTLS")],
"Gebruikers":[check("Nieuwe gebruikers toestaan"),select("Standaardpakket",["basic","business"],"basic"),check("Automatische accountopschorting","Uit")],
"Geavanceerd":[F("Logbewaartermijn (dagen)","number","30"),check("Onderhoudsmodus","Uit"),check("Debuglog","Uit")]
}),
"Aangepaste HTTPD configuraties":L(["Domein","Eigenaar","Configuratie","Status"],hosts.map(x=>[x[0],x[1],"Standaard","Actief"]),["HTTPD configuratie aanpassen","Template bewerken"]),
"DNS Beheer":L(["Domein","Eigenaar","Records","Status"],[["example.nl","demo","5","Actief"],["demo.example.nl","hosting","3","Actief"]],["DNS-zone toevoegen","Records wijzigen","Zone verwijderen"]),
"IP Beheer":L(["IP-adres","Type","Toegewezen aan","Status"],[["192.0.2.10","Gedeeld","2 gebruikers","Actief"],["192.0.2.11","Dedicated","hosting","Actief"]],["IP-adres toevoegen","IP toewijzen"]),
"Naam Servers":S(["Nameservers","Registratie","DNS-overzicht"],{
"Nameservers":[F("Primaire nameserver","text","ns1.example.nl"),F("Secundaire nameserver","text","ns2.example.nl"),F("Primair IP-adres","text","192.0.2.10"),F("Secundair IP-adres","text","192.0.2.11")],
"Registratie":[F("Naam van nameserver"),F("IP-adres"),check("Glue-record registreren")],
"DNS-overzicht":[F("Standaard TTL","number","3600"),F("SOA retry","number","7200")]
}),
"Multi Server Instellen":L(["Server","Adres","Functie","Status"],[["hosting-01","192.0.2.10","Web / DNS","Actief"],["dns-02","192.0.2.11","DNS","Actief"]],["Server toevoegen","Verbinding testen"]),
"PHP Instellingen":S(["PHP-versies","PHP-configuratie","Veiligheid"],{
"PHP-versies":[select("Standaard PHP-versie",["8.4","8.3","8.2"],"8.4"),select("PHP-handler",["PHP-FPM","FastCGI"],"PHP-FPM"),check("Per-domeinversie toestaan")],
"PHP-configuratie":[F("memory_limit","text","256M"),F("upload_max_filesize","text","64M"),F("post_max_size","text","64M"),F("max_execution_time","number","60")],
"Veiligheid":[check("display_errors","Uit"),check("log_errors"),F("disable_functions","text","exec,passthru")]
}),
"Server TLS Certificaat":S(["Certificaat","Let's Encrypt","Eigen certificaat"],{
"Certificaat":[F("Common Name","text","hosting.example.nl"),F("Uitgever","text","Voorbeeld CA"),F("Geldig tot","text","Niet verbonden")],
"Let's Encrypt":[check("Automatisch certificaat aanvragen"),F("E-mailadres","email","admin@example.nl"),select("Validatie",["HTTP-01","DNS-01"],"HTTP-01")],
"Eigen certificaat":[textarea("Certificaat (PEM)"),textarea("Private key (PEM)"),textarea("Certificaatketen (CA bundle)")]
},["Certificaatinstellingen bewaren"]),
"CustomBuild":S(["Software","Versies","Opties","Logboek"],{
"Software":[select("Webserver",["Apache","Nginx","OpenLiteSpeed"],"Apache"),select("PHP-modus",["PHP-FPM","FastCGI"],"PHP-FPM"),select("Database",["MariaDB","MySQL"],"MariaDB")],
"Versies":[select("PHP-versie",["8.4","8.3","8.2"],"8.4"),select("MariaDB-versie",["11.4","10.11"],"11.4"),select("Exim-versie",["Standaard"],"Standaard")],
"Opties":[check("Automatische updates","Uit"),check("Build-logs bewaren"),check("CloudLinux support","Uit")],
"Logboek":[textarea("Recente build-activiteit","Geen buildgegevens beschikbaar in demo.")]
},["Instellingen bewaren","Build voorbereiden"]),
"Back-up en herstel":S(["Back-up maken","Herstellen","Planning","Externe opslag"],{
"Back-up maken":[select("Gebruikers",["Alle gebruikers","Geselecteerde gebruikers"],"Alle gebruikers"),check("Domeinen"),check("Databases"),check("E-mail"),check("DNS"),F("Doelmap","text","/backups")],
"Herstellen":[F("Back-upbestand","file"),select("Herstelmodus",["Volledig","Geselecteerde onderdelen"],"Volledig"),check("Bestaande bestanden overschrijven","Uit")],
"Planning":[select("Frequentie",["Dagelijks","Wekelijks","Maandelijks"],"Dagelijks"),F("Starttijd","time","02:00"),F("Retentie (dagen)","number","14")],
"Externe opslag":[select("Type",["SFTP","S3-compatible","Lokaal"],"SFTP"),F("Host / endpoint"),F("Pad / bucket")]
},["Back-upinstellingen bewaren","Back-up simuleren"]),
"Brute Force Monitor":S(["Geblokkeerde IP's","Aanmeldpogingen","Instellingen"],{
"Geblokkeerde IP's":[F("IP-adres","text","203.0.113.12"),F("Reden","text","Mislukte aanmeldpogingen"),F("Geblokkeerd sinds","text","Vandaag")],
"Aanmeldpogingen":[F("Periode","text","Laatste 24 uur"),F("Aantal mislukte pogingen","number","27")],
"Instellingen":[F("Pogingen voor blokkade","number","5"),F("Blokkeertijd (minuten)","number","60"),check("Waarschuwingen verzenden")]
},["Instellingen bewaren","IP-blokkade toevoegen"]),
"Proces Monitor":M(["PID","Proces","Gebruiker","CPU","Geheugen"],[["1024","httpd","www-data","1.2%","80 MB"],["1190","mariadbd","mysql","2.4%","310 MB"],["1428","exim","mail","0.1%","24 MB"]]),
"Beheer van e-mail wachtrij":L(["ID","Van","Naar","Leeftijd","Status"],[["1A2B3C","no-reply@example.nl","demo@example.nl","5 min","Wachtrij"],["2B3C4D","admin@example.nl","hosting@example.nl","12 min","Uitgesteld"]],["Wachtrij vernieuwen","Opnieuw proberen","Verwijderen"]),
"Systeem Services":M(["Service","Beschrijving","Status","Geheugen","Starttype"],[["httpd","Apache HTTP Server","Actief","115 MB","Automatisch"],["named","DNS Server","Actief","42 MB","Automatisch"],["mariadb","Database Server","Actief","312 MB","Automatisch"],["exim","E-mail","Actief","28 MB","Automatisch"],["sshd","SSH","Actief","14 MB","Automatisch"]]),
"Systeem Backup":S(["Back-upbestanden","Back-upplanning","Herstelopties"],{
"Back-upbestanden":[F("Locatie","text","/backup"),select("Back-upinhoud",["Volledig systeem","Configuratie"],"Volledig systeem")],
"Back-upplanning":[select("Interval",["Dagelijks","Wekelijks","Handmatig"],"Wekelijks"),F("Bewaar aantal","number","7")],
"Herstelopties":[check("Instellingen herstellen"),check("Gebruikersgegevens herstellen"),check("Databases herstellen")]
},["Instellingen bewaren","Back-up starten"]),
"cPanel Importeren":P([F("Bronserver"),F("cPanel-gebruikersnaam"),F("Back-upbestand / pad"),select("Doelgebruiker",["Nieuw account","Bestaand account"],"Nieuw account"),check("E-mail migreren"),check("Databases migreren"),check("DNS overnemen")],["Import voorbereiden"]),
"Bericht aan Alle Gebruikers":P([F("Onderwerp"),textarea("Bericht"),select("Ontvangers",["Alle gebruikers","Resellers","Administrators"],"Alle gebruikers"),check("Ook per e-mail verzenden")],["Voorbeeld bekijken","Bericht opstellen"]),
"Thema’s":L(["Thema","Versie","Standaard","Status"],[["Evolution","1.0","Ja","Actief"],["Enhanced","1.0","Nee","Beschikbaar"]],["Thema instellen","Thema uploaden"]),
"Evolution Thema Aanpassen":S(["Algemeen","Kleuren","Logo","Menu"],{
"Algemeen":[F("Paneeltitel","text","HostingPanel"),select("Weergave",["Modern","Traditioneel"],"Modern"),select("Layout",["Sidebar","Top navigatie"],"Sidebar")],
"Kleuren":[F("Primaire kleur","color","#2373d1"),F("Navigatiekleur","color","#1c304f"),F("Accentkleur","color","#55b98b")],
"Logo":[F("Logobestand","file"),F("Favicon","file"),check("Toon paneelnaam")],
"Menu":[check("Snelle navigatie"),check("Zoekbalk"),check("Dashboard widgets")]
}),
"Onderhoud":S(["Systeemstatus","Onderhoudsmodus","Opruimen"],{
"Systeemstatus":[F("DirectAdmin versie","text","Niet verbonden"),F("Laatste update","text","Onbekend"),F("Beschikbare schijfruimte","text","Demo")],
"Onderhoudsmodus":[check("Onderhoudsmodus inschakelen","Uit"),textarea("Onderhoudsbericht","De server is tijdelijk in onderhoud.")],
"Opruimen":[check("Tijdelijke bestanden opruimen","Uit"),check("Oude logs opruimen","Uit"),F("Logretentie (dagen)","number","30")]
},["Instellingen bewaren","Onderhoud simuleren"]),
"Cron Taken van alle Gebruikers":L(["Gebruiker","Minuut","Uur","Dag","Commando"],[["demo","0","2","*","/usr/local/bin/backup"],["hosting","*/5","*","*","/usr/bin/php cron.php"]],["Cron taak toevoegen","Bewerken"]),
"Systeem bestand editor":S(["Bestandseditor","Bestandsrechten"],{
"Bestandseditor":[F("Bestandspad","text","/etc/hosts"),textarea("Inhoud","127.0.0.1 localhost\n192.0.2.10 hosting.example.nl")],
"Bestandsrechten":[F("Eigenaar","text","root"),F("Groep","text","root"),F("Mode","text","0644")]
},["Concept bewaren","Bestand selecteren"]),
"Bekijk Logboek":S(["Systeemlog","Webserver","Authenticatie","Mail"],{
"Systeemlog":[textarea("Logregels","[DEMO] Service gestart\n[DEMO] Configuratie geladen")],
"Webserver":[textarea("HTTPD-log","[DEMO] GET / 200\n[DEMO] GET /favicon.ico 200")],
"Authenticatie":[textarea("Authenticatielog","[DEMO] Beheerder aangemeld")],
"Mail":[textarea("Maillog","[DEMO] Bericht afgeleverd")]
},["Filter toepassen","Log downloaden"]),
"Globale Statistieken":M(["Onderdeel","Huidig gebruik","Limiet","Status"],[["Schijfruimte","42 GB","100 GB","42%"],["Bandbreedte","128 GB","500 GB","26%"],["Gebruikers","24","100","24%"],["Domeinen","38","100","38%"]]),
"Plugin Beheer":L(["Plugin","Versie","Auteur","Status"],[["File Manager","1.0","HostingPanel","Actief"],["DNS Tools","1.0","HostingPanel","Beschikbaar"],["Webmail","1.0","HostingPanel","Beschikbaar"]],["Plugin uploaden","Inschakelen","Verwijderen"]),
"ConfigServer Security & Firewall":S(["Firewallstatus","Toegestane poorten","IP-adressen","Configuratie"],{
"Firewallstatus":[check("Firewall ingeschakeld"),check("Login failure daemon"),F("Blokkades","number","3")],
"Toegestane poorten":[F("TCP inkomend","text","22,80,443,2222"),F("TCP uitgaand","text","25,53,80,443,587"),F("UDP inkomend","text","53")],
"IP-adressen":[F("IP-adres"),select("Actie",["Toestaan","Blokkeren"],"Toestaan"),textarea("Opmerking")],
"Configuratie":[F("Loginlimiet","number","5"),F("Blokkeertijd","number","3600")]
}),
"Beheer Tickets":L(["Ticket","Onderwerp","Afzender","Prioriteit","Status"],[["#1001","Vraag over SSL","demo","Normaal","Open"],["#1002","DNS wijziging","hosting","Laag","Gesloten"]],["Ticket aanmaken","Beantwoorden"]),
"Ticket Aanmaken":P([F("Onderwerp"),select("Prioriteit",["Laag","Normaal","Hoog","Urgent"],"Normaal"),select("Ontvanger",["Support","Administrator"],"Support"),textarea("Bericht"),F("Bijlage","file")],["Ticket opstellen"]),
"Updates":M(["Component","Geïnstalleerd","Beschikbaar","Status"],[["HostingPanel","Prototype","—","Demo"],["Besturingssysteem","—","—","Niet gekoppeld"],["Webserver","—","—","Niet gekoppeld"]]),
"Licensering":S(["Licentieoverzicht","Licentiegegevens"],{
"Licentieoverzicht":[F("Product","text","HostingPanel UI"),F("Licentiemodel","text","Open source prototype"),F("Status","text","Geen serverlicentie nodig")],
"Licentiegegevens":[F("Licentiesleutel"),F("Licentiehouder"),F("Server-ID")]
},["Controleren"]),
"Domeinbeheer":L(["Domein","Eigenaar","Document root","SSL","Status"],hosts.map(x=>[x[0],x[1],"/domains/"+x[0]+"/public_html","Actief","Actief"]),["Domein toevoegen","Subdomein toevoegen","Domeinwijziging"]),
"E-mailaccounts":L(["Adres","Domein","Quotum","Gebruik","Status"],[["info@example.nl","example.nl","1 GB","120 MB","Actief"],["support@example.nl","example.nl","2 GB","340 MB","Actief"]],["E-mailaccount maken","Forwarder maken","Autoresponder"]),
"Bestandsbeheer":L(["Naam","Type","Grootte","Laatst gewijzigd"],[["domains","Map","—","Vandaag"],["backups","Map","—","Gisteren"],["public_html","Map","—","Vandaag"]],["Nieuwe map","Uploaden","Rechten wijzigen"]),
"Databases":L(["Naam","Type","Gebruiker","Grootte"],[["example_db","MariaDB","example_user","18 MB"],["demo_db","MariaDB","demo_user","43 MB"]],["Database maken","Gebruiker maken","phpMyAdmin"]),
"SSL certificaten":L(["Domein","Uitgever","Verloopt","Status"],[["example.nl","Let's Encrypt","Over 60 dagen","Actief"],["demo.example.nl","Let's Encrypt","Over 45 dagen","Actief"]],["Certificaat aanvragen","Handmatig certificaat"]),
"Cron Taken":L(["Minuut","Uur","Dag","Maand","Commando"],[["0","2","*","*","/usr/local/bin/backup"]],["Cron taak aanmaken"]),
"Back-ups":L(["Bestand","Datum","Grootte","Type"],[["backup-example.tar.gz","Vandaag","154 MB","Volledig"],["backup-demo.tar.gz","Gisteren","106 MB","Volledig"]],["Back-up maken","Herstellen"])
};
const modalFields={
"Gebruikerspakket":[F("Pakketnaam"),F("Schijfruimte (MB)","number","5120"),F("Bandbreedte (MB)","number","51200"),F("Maximaal aantal domeinen","number","5"),F("Maximaal aantal mailaccounts","number","10")],
"DNS-record":[select("Type",["A","AAAA","CNAME","MX","TXT","NS","CAA"],"A"),F("Naam"),F("Waarde"),F("TTL (seconden)","number","3600"),F("Prioriteit","number","10")],
"IP-adres":[F("IP-adres"),select("Type",["Gedeeld","Dedicated"],"Gedeeld"),select("Gebruiker",["admin","demo","hosting"],"admin")],
"Domein":[F("Domeinnaam"),select("Gebruiker",["demo","hosting"],"demo"),check("SSL inschakelen"),check("DNS-zone aanmaken")],
"E-mailaccount":[F("E-mailadres","email"),F("Wachtwoord","password"),F("Quotum (MB)","number","1024")],
"Database":[F("Databasenaam"),F("Gebruiker"),F("Wachtwoord","password"),select("Type",["MariaDB","MySQL"],"MariaDB")],
"Ticket":[F("Onderwerp"),select("Prioriteit",["Laag","Normaal","Hoog"],"Normaal"),textarea("Bericht")],
"Algemeen":[F("Naam"),textarea("Omschrijving")],
"Bestand":[F("Naam"),F("Bestand","file")],
"Cron":[F("Minuut","text","0"),F("Uur","text","2"),F("Dag van maand","text","*"),F("Maand","text","*"),F("Weekdag","text","*"),F("Commando")],
"Server":[F("Servernaam"),F("IP-adres"),F("API-poort","number","2222"),select("Rol",["Webserver","DNS","Mail","Database"],"Webserver")],
"Certificaat":[F("Domeinnaam"),check("Automatisch verlengen"),select("Methode",["HTTP-01","DNS-01"],"HTTP-01")],
"Template":[F("Sjabloonnaam"),F("Onderwerp"),textarea("Inhoud")],
"Gebruiker":[F("Gebruikersnaam"),F("E-mailadres","email"),F("Wachtwoord","password"),select("Pakket",["basic","business"],"basic")]
};
const targetForAction=(action)=>{
 const a=action.toLowerCase();
 if(a.includes("pakket"))return "Gebruikerspakket";
 if(a.includes("record")||a.includes("zone"))return "DNS-record";
 if(a.includes("ip"))return "IP-adres";
 if(a.includes("domein")||a.includes("subdomein"))return "Domein";
 if(a.includes("mail")||a.includes("forward")||a.includes("autoresponder"))return "E-mailaccount";
 if(a.includes("database"))return "Database";
 if(a.includes("ticket"))return "Ticket";
 if(a.includes("cron"))return "Cron";
 if(a.includes("server"))return "Server";
 if(a.includes("certificaat"))return "Certificaat";
 if(a.includes("sjabloon")||a.includes("template"))return "Template";
 if(a.includes("gebruiker")||a.includes("reseller")||a.includes("admin"))return "Gebruiker";
 if(a.includes("bestand")||a.includes("upload")||a.includes("map"))return "Bestand";
 return "Algemeen";
};
let pageTab="",pageMode="index",pageFilter="";
const nativeRender=render,nativeGo=go;
go=function(label){pageTab="";pageMode="index";pageFilter="";nativeGo(label);};
function heading(title,description){return '<div class="heading"><div><div class="eyebrow">DirectAdmin / '+escapeHtml(role)+'</div><h1>'+escapeHtml(title)+'</h1><div class="subhead">'+escapeHtml(description)+'</div></div><div class="heading-actions"><button class="btn" data-go="Dashboard">← Dashboard</button></div></div>';}
const hint='<span class="demo-hint">● Interfacevoorbeeld · geen backend</span>';
function fieldHtml(field,index){
 const id="field"+index,label=escapeHtml(field.label),value=escapeHtml(field.value||"");
 let control="";
 if(field.type==="select") control='<select id="'+id+'" class="page-input">'+(field.options||[]).map(o=>'<option '+(o===field.value?"selected":"")+'>'+escapeHtml(o)+'</option>').join("")+'</select>';
 else if(field.type==="check")control='<label class="page-switch"><input id="'+id+'" type="checkbox" '+(field.value!=="Uit"?"checked":"")+'><span></span><small>'+(field.value==="Uit"?"Uitgeschakeld":"Ingeschakeld")+'</small></label>';
 else if(field.type==="textarea")control='<textarea id="'+id+'" class="page-input" rows="6">'+value+'</textarea>';
 else control='<input id="'+id+'" class="page-input" type="'+escapeHtml(field.type)+'" '+(field.type==="file"?'':'value="'+value+'"')+' autocomplete="off">';
 return '<div class="page-field"><label for="'+id+'">'+label+'</label>'+control+'</div>';
}
function formCard(fields,actions,intro){
 return '<div class="panel page-panel"><div class="panel-head"><div class="panel-title"><span>▤</span>'+escapeHtml(intro||"Instellingen")+'</div>'+hint+'</div><form class="page-form" id="pageForm" autocomplete="off"><div class="page-fields">'+fields.map(fieldHtml).join("")+'</div><div class="page-actions">'+actions.map((a,i)=>'<button type="'+(i===0?'submit':'button')+'" class="btn '+(i===0?"primary":"")+'" data-page-form-action="'+escapeHtml(a)+'">'+escapeHtml(a)+'</button>').join("")+'</div></form></div>';
}
function listCard(def,title){
 const cols=def.columns;
 const needle=pageFilter.toLocaleLowerCase("nl");
 const rows=def.rows.filter(r=>r.some(c=>String(c).toLocaleLowerCase("nl").includes(needle)));
 return '<div class="panel"><div class="panel-head"><div class="panel-title"><span>▤</span>'+escapeHtml(title)+' <span class="record-count">'+rows.length+' records</span></div>'+hint+'</div><div class="page-list-toolbar"><input id="pageFilter" class="page-input" placeholder="Zoeken in overzicht..." value="'+escapeHtml(pageFilter)+'" aria-label="Zoeken in overzicht"><button class="btn" data-page-clear>Wis filter</button></div><div class="page-table-wrap"><table class="data-table"><thead><tr><th><input type="checkbox" aria-label="Selecteer alles"></th>'+cols.map(c=>'<th>'+escapeHtml(c)+'</th>').join("")+'<th>Acties</th></tr></thead><tbody>'+rows.map((row,i)=>'<tr><td><input type="checkbox" aria-label="Selecteer rij '+(i+1)+'"></td>'+row.map(c=>'<td>'+((c==="Actief"||c==="Open")?'<span class="status">'+escapeHtml(c)+'</span>':escapeHtml(c))+'</td>').join("")+'<td><button class="btn btn-mini" data-page-edit="'+escapeHtml(row[0])+'">Bewerken</button></td></tr>').join("")+'</tbody></table>'+(rows.length?"":'<div class="empty">Geen resultaten.</div>')+'</div></div>';
}
function mainPage(title,def){
 let description="Beheer en configureer "+title.toLocaleLowerCase("nl")+".";
 const actions=def.actions||[];
 let html=heading(title,description);
 if(def.kind==="settings"){
 const names=def.tabs,tab=names.includes(pageTab)?pageTab:names[0];
 html+='<div class="page-tabs" role="tablist">'+names.map(n=>'<button class="page-tab '+(n===tab?"selected":"")+'" data-page-tab="'+escapeHtml(n)+'" role="tab" aria-selected="'+(n===tab)+'">'+escapeHtml(n)+'</button>').join("")+'</div>'+formCard(def.fields[tab],actions,tab);
 }else if(def.kind==="form")html+=formCard(def.fields,actions,title);
 else {
 html+='<div class="page-commands">'+actions.map((a,i)=>'<button class="btn '+(i===0?"primary":"")+'" data-page-command="'+escapeHtml(a)+'">'+(i===0?"＋ ":"")+escapeHtml(a)+'</button>').join("")+'</div>'+listCard(def,title);
 }
 return html+'<div class="page-note">De inhoud en voorbeeldrecords zijn voor de UI ontworpen. Er is geen daadwerkelijke verbinding met een DirectAdmin-server.</div>';
}
function secondary(title,record){
 const fields=modalFields[targetForAction(record)]||modalFields.Algemeen;
 return heading(title,"Interfaceformulier voor "+record.toLocaleLowerCase("nl")+".")+
 '<div class="page-commands"><button class="btn" data-page-return>← Terug naar overzicht</button></div>'+
 formCard(fields,["Concept bewaren","Annuleren"],record);
}
function draw(){
 if(current==="Dashboard"||current==="Zoeken"){nativeRender();return;}
 renderSidebar();$("breadcrumb").textContent=current;$("roleButton").textContent=role+" ▾";
 const def=catalog[current];
 $("content").innerHTML=(def ? (pageMode==="index"?mainPage(current,def):secondary(current,pageMode)) : (visibleSections().some(s=>s.title===current)?categoryView(current):heading(current,"Scherm is opgenomen in de navigatie.")+formCard(modalFields.Algemeen,["Concept bewaren"],current)))+'<footer>HostingPanel · Compleet navigatieprototype · Geen live backendverbinding</footer>';
}
render=draw;
document.addEventListener("click",function(e){
 const tab=e.target.closest("[data-page-tab]");if(tab){pageTab=tab.dataset.pageTab;draw();return;}
 const command=e.target.closest("[data-page-command]");if(command){const a=command.dataset.pageCommand;if(/vernieuw|filter|herstart|verwijderen|schorsen|download|test|controleren|inschakelen|opnieuw|configuratie|wijziging/i.test(a)&&!/toevoegen|maken|aanmaken|bewerken|aanpassen|wijzigen/.test(a)){toast("Actie '"+a+"' is een UI-demo. Er is niets gewijzigd.");return;}pageMode=a;draw();return;}
 const edit=e.target.closest("[data-page-edit]");if(edit){pageMode="Bewerken: "+edit.dataset.pageEdit;draw();return;}
 if(e.target.closest("[data-page-return]")){pageMode="index";draw();return;}
 if(e.target.closest("[data-page-clear]")){pageFilter="";draw();return;}
});
document.addEventListener("input",function(e){if(e.target.id==="pageFilter"){pageFilter=e.target.value;const cursor=e.target.selectionStart;draw();const input=$("pageFilter");if(input){input.focus();input.setSelectionRange(cursor,cursor);}}});
document.addEventListener("submit",function(e){if(e.target.id!=="pageForm")return;e.preventDefault();toast("Voorbeeldformulier: er is niets opgeslagen of verzonden.");});
document.addEventListener("click",function(e){const btn=e.target.closest("[data-page-form-action]");if(!btn)return;if(btn.type==="button"){if(btn.dataset.pageFormAction==="Annuleren"){pageMode="index";draw();}else toast("Actie '"+btn.dataset.pageFormAction+"' is alleen een UI-voorbeeld.");}});
draw();
})();