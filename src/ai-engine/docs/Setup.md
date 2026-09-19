# Setup for Local LLM

``` powershell
# paste this in Powershell
irm https://ollama.com/install.ps1 | iex

# check if installation is successful (please restart the terminal)
ollama --version

# test with interaction mode
ollama run ollama run gemma3:1b # try promting something if ">>>" presents

# to leave
/bye

# health check (please use another terminal window)
curl http://localhost:11434

# run server in the background
ollama serve
```


