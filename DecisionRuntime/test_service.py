import unittest
from unittest.mock import patch

from service import Handler, decode_request, normalize


class ContractTests(unittest.TestCase):
    def test_access_log_omits_success_but_keeps_failures(self):
        handler = Handler.__new__(Handler)
        handler.requestline = 'POST /v1/decisions HTTP/1.1'
        with patch.object(handler, 'log_message') as log:
            handler.log_request(200)
            log.assert_not_called()
            handler.log_request(502)
            log.assert_called_once()

    def test_choice_preserves_option_order(self):
        request = decode_request({
            "evidence": "The umbrella is red.", "question": "What color?",
            "type": "choice", "options": ["blue", "red"], "evaluator": "startlux",
        })
        result = normalize(request, {"red": 0.9, "blue": 0.1}, {})
        self.assertEqual(result["answer"], "red")
        self.assertEqual(result["probabilities"], [0.1, 0.9])

    def test_noul_answer_and_order(self):
        request = decode_request({
            "evidence": "", "question": "Is it red?", "type": "noul",
            "options": [], "evaluator": "jev",
        })
        result = normalize(request, {"true": 0.1, "false": 0.9}, {})
        self.assertIs(result["answer"], False)
        self.assertEqual(result["probabilities"], [0.9, 0.1])

    def test_score_uses_probability_weighted_index(self):
        request = decode_request({
            "evidence": "", "question": "How severe?", "type": "score",
            "options": ["minor", "critical"], "evaluator": "jev",
        })
        self.assertEqual(normalize(request, {"0": 0.2, "1": 0.8}, {})["answer"], 0.8)

    def test_rejects_duplicate_choice_options_and_invalid_probabilities(self):
        payload = {
            "evidence": "", "question": "Which?", "type": "choice",
            "options": ["a", "b"], "evaluator": "jev",
        }
        with self.assertRaises(ValueError):
            decode_request(dict(payload, options=["a", "a"]))
        with self.assertRaises(ValueError):
            normalize(decode_request(payload), {"a": float("nan"), "b": 0.1}, {})


if __name__ == "__main__":
    unittest.main()
