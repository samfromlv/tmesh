docker pull <your docker server>/tbot
docker run --rm \
	--name tbot \
	-v /home/dev/tbot/config:/tbot/config \
	-v /home/dev/tbot/data:/tbot/data \
	--network host \
	<your docker server>/tbot