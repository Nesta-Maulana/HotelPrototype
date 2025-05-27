#!/bin/bash

# Wait for data import to complete
echo "Waiting for data import to complete..."

# Check if marker file exists (created by importer when done)
while [ ! -f /app/import-complete.marker ]; do
    echo "Data import in progress..."
    sleep 5
done

echo "Data import completed! Starting web application..."

# Start the web application
exec dotnet HotelSearchApp.Web.dll