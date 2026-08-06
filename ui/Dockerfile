# Node 24: Angular 22 requires ^22.22.3 || ^24.15.0 || >=26. The previous image ran Node 18,
# which cannot build this app at all.
FROM --platform=$BUILDPLATFORM node:24-alpine AS build

WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginx:alpine AS runtime

COPY --from=build /app/dist/pirouter-ui/browser /usr/share/nginx/html

# The nginx image renders anything in this directory with envsubst at startup, which is how
# the LAN address and ports reach the config without being baked into the image.
COPY nginx.conf.template /etc/nginx/templates/default.conf.template

ENV LAN_IP=127.0.0.1 \
    UI_PORT=4200 \
    API_PORT=51508

EXPOSE 4200

CMD ["nginx", "-g", "daemon off;"]
