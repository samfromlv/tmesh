docker pull <your docker server>/tbot
docker rm -f tbot
docker run -d \
  --name tbot \
  --restart unless-stopped \
  -v /home/dev/tbot/config:/tbot/config \
  -v /home/dev/tbot/data:/tbot/data \
  --network host \
  <your docker server>/tbot