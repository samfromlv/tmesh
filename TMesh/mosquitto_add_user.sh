sudo docker exec -it mosquitto sh
##################################################
# inside container:
cd /mosquitto/config
#!aaaaaa your node id (with !), you will be asked to set password
mosquitto_passwd ./pwd !aaaaaaa
