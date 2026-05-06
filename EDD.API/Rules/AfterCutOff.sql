-- After 17:00 UTC cutoff: add 1 calendar day (pickupTime in RuleEngine is UTC HH:mm from PickupDate).
INSERT INTO public.edd_rules (
  id,
  name,
  priority,
  is_active,
  rule_type,
  rule_json,
  version,
  created_at
)
VALUES (
  gen_random_uuid(),
  'After 17:00 UTC cutoff +1 day',
  10,
  true,
  'definition',
  '{
    "conditions": [
      { "field": "pickupTime", "operator": "greater_than", "value": "17:00" }
    ],
    "actions": [
      { "type": "add_days", "value": 1 }
    ]
  }'::jsonb,
  '1',
  now()
);
