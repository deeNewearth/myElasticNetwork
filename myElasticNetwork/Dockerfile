FROM microsoft/dotnet:2.0-runtime
MAINTAINER shree dee <dee@labizbille.com>

ENV isManager false
ENV serverName none
ENV serverPublicIp none
ENV s3BucketName none
ENV s3AccessKey none
ENV s3AccessId none
ENV serverPrivateIp none


# Remove SUID programs
RUN for i in `find / -perm +6000 -type f 2>/dev/null`; do chmod a-s $i; done

RUN echo "deb http://http.debian.net/debian experimental main" >> /etc/apt/sources.list && \
    apt-get update && \
    DEBIAN_FRONTEND=noninteractive apt-get install -y net-tools && \
    DEBIAN_FRONTEND=noninteractive apt-get install -y -t experimental tinc && \
    apt-get clean && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*

EXPOSE 655/tcp 655/udp
VOLUME /etc/tinc

WORKDIR /app

COPY bin/Release/PublishOutput/* ./
#COPY PublishOutput/* ./

ENTRYPOINT ["dotnet", "myElasticNetwork.dll"]


