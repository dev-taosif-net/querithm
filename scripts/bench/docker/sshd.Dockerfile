# SSH bastion for the tunnel tests: user querithm, password from compose.yml (build arg SSH_PASSWORD).
# From the bastion the databases are reachable as mssql:1433 and postgres:5432 (compose service names).
FROM alpine:3.20
ARG SSH_PASSWORD
# Built outside compose the arg is empty and the password tests would fail for no visible reason.
RUN test -n "$SSH_PASSWORD" || { echo "build with compose.yml (build arg SSH_PASSWORD)" >&2; exit 1; }
RUN apk add --no-cache openssh \
 && ssh-keygen -A \
 && adduser -D -s /bin/sh querithm \
 && echo "querithm:${SSH_PASSWORD}" | chpasswd \
 && mkdir -p /home/querithm/.ssh \
 && sed -i -e 's/^#\?AllowTcpForwarding.*/AllowTcpForwarding yes/' -e 's/^#\?PasswordAuthentication.*/PasswordAuthentication yes/' /etc/ssh/sshd_config \
 && grep -q '^AllowTcpForwarding yes' /etc/ssh/sshd_config || echo 'AllowTcpForwarding yes' >> /etc/ssh/sshd_config
COPY ssh/authorized_keys /home/querithm/.ssh/authorized_keys
RUN chown -R querithm:querithm /home/querithm/.ssh && chmod 700 /home/querithm/.ssh && chmod 600 /home/querithm/.ssh/authorized_keys
EXPOSE 22
CMD ["/usr/sbin/sshd", "-D", "-e"]
