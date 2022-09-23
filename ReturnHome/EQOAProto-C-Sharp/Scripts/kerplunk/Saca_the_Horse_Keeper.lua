-- Saca the Horse Keeper

local coaches = require('Scripts/ports')

local playerCoaches = {
   grobb_coach = "Get me a horse to grobb.",
   oggok_coach = "Get me a horse to oggok.",
   south_crossroads_coach = "Get me a horse to da south crossroads.",
}

local dialogueOptions = {}
local ch = tostring(choice)
function event_say()
   if(GetPlayerFlags(mySession, "kerplunk_coach")) then
      if (ch:find("south crossroads")) then
         TeleportPlayer(mySession,GetWorld(coaches.south_crossroads.world),coaches.south_crossroads.x,coaches.south_crossroads.y,coaches.south_crossroads.z,coaches.south_crossroads.facing)
      elseif (ch:find("grobb")) then
         TeleportPlayer(mySession,GetWorld(coaches.grobb.world),coaches.grobb.x,coaches.grobb.y,coaches.grobb.z,coaches.grobb.facing)
      elseif (ch:find("oggok")) then
         TeleportPlayer(mySession,GetWorld(coaches.oggok.world),coaches.oggok.x,coaches.oggok.y,coaches.oggok.z,coaches.oggok.facing)
      else
         npcDialogue = "Where would you like to go?"
         for coach, diag in pairs(playerCoaches) do
            if (GetPlayerFlags(mySession, coach) or GetPlayerFlags(mySession, "admin")) then
               table.insert(dialogueOptions, diag)
            end
         end
         SendDialogue(mySession, npcDialogue, dialogueOptions)
      end
   else
      if (ch:find("Yes")) then
         npcDialogue = "Excellent, you can now use this coach any time."
         SetPlayerFlags(mySession, "kerplunk_coach", true)
         SendDialogue(mySession, npcDialogue, dialogueOptions)
      elseif (ch:find("No")) then
         npcDialogue = "If you aren't interested then why are you wasting my time."
         SendDialogue(mySession, npcDialogue, dialogueOptions)
      else
         npcDialogue = "Would you like to sign the coachman's ledger?"
         dialogueOptions = {"Yes", "No"}
         SendDialogue(mySession, npcDialogue, dialogueOptions)
      end
   end
end
