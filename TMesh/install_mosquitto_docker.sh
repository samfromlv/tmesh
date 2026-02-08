#install docker https://docs.docker.com/engine/install/ubuntu/

cd /home/dev
mkdir mosquitto
mkdir mosquitto/config
mkdir mosquitto/data
mkdir mosquitto/log

sudo docker pull eclipse-mosquitto:latest
sudo docker rm -f mosquitto
sudo docker run -d \
--name mosquitto \
--restart unless-stopped \
--network host \
-v "/home/dev/mosquitto/config:/mosquitto/config" \
-v "/home/dev/mosquitto/data:/mosquitto/data" \
-v "/home/dev/mosquitto/log:/mosquitto/log" \
eclipse-mosquitto