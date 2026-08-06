FROM alpine:3.21

RUN apk add --no-cache dnsmasq

# The real configuration is written by the API into the mounted /etc/dnsmasq.d. This file
# only sets up the essentials and pulls that directory in.
COPY dnsmasq.conf /etc/dnsmasq.conf

# -k keeps it in the foreground so Docker supervises the process directly, which in turn is
# what lets the API find it in /proc and send it SIGHUP.
CMD ["dnsmasq", "-k", "--log-facility=-"]
