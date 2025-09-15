# ConvertPdf

A simple webservice for converting PDF files to **text** or **JSON**, written in C# using [iText](https://itextpdf.com/).

Licensed under **GPL**.

---

## Features

* Convert PDF → plain text
* Convert PDF → structured JSON
* Run locally or on a Linux VPS
* Simple to deploy and run behind **nginx**

---

## Development & Testing

1. Open `ConvertPdf` in Visual Studio.
2. Test locally:

   * **Console App** → for local files.
   * **Webservice Project** → for API endpoints.

---

## Deployment Guide (Linux VPS)

### 1. Reverse Proxy (nginx)

Example config (`/etc/nginx/sites-available/convertpdf.nginx`):

```nginx
# Test config:  sudo nginx -t
# Reload:      sudo service nginx restart
# Add HTTPS:   sudo certbot --nginx
# Enable site: sudo ln -s /etc/nginx/sites-available/convertpdf.nginx /etc/nginx/sites-enabled/

server {
    listen 80;
    listen [::]:80;

    server_name pdf.yourserver.com;

    location / {
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_pass         http://127.0.0.1:4399;
    }
}
```

---

### 2. Deploying Code

Upload your zip (`deploy_convertpdf.zip`) to the server and run:

```bash
if [[ ! -f deploy_convertpdf.zip ]] ; then
    echo 'File "deploy_convertpdf.zip" is not there, aborting.'
    exit
fi

unzip deploy_convertpdf.zip
rm codeBackup/deploy_convertpdf.zip
mv deploy_convertpdf.zip codeBackup/deploy_convertpdf.zip

rm -rf /var/www/convertpdf_backup
mv /var/www/convertpdf /var/www/convertpdf_backup

mv ./ConvertPdf /var/www/convertpdf
find /var/www/convertpdf -type d -exec chmod 755 {} +
find /var/www/convertpdf -type f -exec chmod 644 {} +

pm2 restart convertpdf
echo "✅ Deployment complete!"
```

---

### 3. Run Service with pm2

Start the service (once):

```bash
sudo pm2 start dotnet --name convertpdf \
  --log /var/log/dotnet/pm2-convertpdf.log \
  -- /var/www/convertpdf/ConvertPdf.dll --urls="http://*:4399;"
```

For more pm2 info: [pm2 quick start](https://pm2.keymetrics.io/docs/usage/quick-start/).

---

## Notes

* Port `4399` is the internal port (nginx forwards to this).
* HTTPS is handled by **nginx + certbot**.
* Logs are in `/var/log/dotnet/pm2-convertpdf.log`.
