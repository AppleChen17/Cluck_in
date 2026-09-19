"""Outbound side of the external module: replies, posts and reactions.

The inbound half (adapters/) normalizes what arrives. This half sends what the
app decides to say. They share the reply registry, which is the only thing
linking one to the other.
"""
