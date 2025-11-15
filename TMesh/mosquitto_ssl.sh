openssl req -new -x509 -days 3650 -extensions v3_ca -keyout ca.key -out ca.crt
openssl genrsa -aes256 -out server.key 2048
openssl genrsa -out server.key 2048
openssl req -out server.csr -key server.key -new
#When prompted for the CN (Common Name), please enter either your server (or broker) hostname or domain name.

openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out server.crt -days 3650
