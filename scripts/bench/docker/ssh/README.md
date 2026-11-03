# SSH fixture keys

Development fixture only (like the passwords in `compose.yml`): these keys open the `ssh` test container and nothing
else. `test_ed25519` has no passphrase; `test_rsa_pass` uses the passphrase in `test_rsa_pass.passphrase` (the tests
read it from there). Never use them anywhere real.
