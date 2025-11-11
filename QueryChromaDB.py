import chromadb
import requests
import logging

# Configure logging
logging.basicConfig(level=logging.INFO)
logging.getLogger('chromadb').setLevel(logging.WARNING)

client = chromadb.HttpClient(host='http://localhost:8000')
collection = client.get_collection('collection')
print(f'Number of items in collection: {collection.count()}')
if collection.count() > 0:
    print('Sample data:', collection.get(limit=1))

# Embedding API details
embedding_api_url = "https://192.168.118.23/v1/embeddings"
text_model = "bge-m3"

def embed_text(text):
    payload = {"model": text_model, "input": text}
    response = requests.post(embedding_api_url, json=payload, verify=False)
    if response.status_code == 200:
        data = response.json()
        return data["data"][0]["embedding"]
    else:
        raise Exception(f"Embedding failed: {response.text}")

# Example query
query = "What are blackholes?"
query_embedding = embed_text(query)
results = collection.query(query_embeddings=[query_embedding], n_results=3)

print("\nTop 3 results for query:", query)
for i in range(len(results['ids'][0])):
    id = results['ids'][0][i]
    metadata = results['metadatas'][0][i]
    distance = results['distances'][0][i]
    text_preview = metadata['chunk'][:100] + "..." if len(metadata['chunk']) > 100 else metadata['chunk']
    print(f"{i+1}. ID: {id}, Distance: {distance:.4f}, Text: {text_preview}")