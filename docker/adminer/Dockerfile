FROM adminer:5.5.0-standalone

# Adminer 5.5.0 already builds FreeTDS and pdo_dblib into its official image.
# Fail the derivative build if that MSSQL driver ever disappears upstream.
RUN php -m | grep -Fx pdo_dblib

