function event_say()
diagOptions = {}
    npcDialogue = "Hello playerName. I can set you up for a one way trip to Arcadin if you'd like."
SendDialogue(mySession, npcDialogue, diagOptions)
end