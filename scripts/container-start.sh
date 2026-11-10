#!/bin/sh
# Starts the Test Lab API and the web app in one container; if either exits, the container stops.
set -e
: "${TESTLAB_KEY:=$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n')}"
export TESTLAB_KEY

cd /app/api
Urls=http://127.0.0.1:5110 dotnet testlab.dll serve &
api=$!

cd /app/web
node server.js &
web=$!

trap 'kill $api $web 2>/dev/null' TERM INT
while kill -0 $api 2>/dev/null && kill -0 $web 2>/dev/null; do sleep 2; done
kill $api $web 2>/dev/null
exit 1
