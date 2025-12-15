docker pull <your docker server>/tbot
docker rm -f tbot
docker run --rm \
	--name tbot \
	-v /home/dev/tbot/data:/tbot/data \
	--network host \
    --env TBOT_CONFIG_PATH=/tbot/app/appsettings.json \
	<your docker server>/tbot \
	/updatedb