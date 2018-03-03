@echo off
docker login
docker build -t labizbille/elasticnetwork .
docker tag labizbille/elasticnetwork labizbille/elasticnetwork:latest
docker push labizbille/elasticnetwork
pause